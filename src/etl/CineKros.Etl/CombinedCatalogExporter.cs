using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CineKros.Etl;

internal sealed record CombinedCatalogOptions(string SemanticDirectory, string EnrichmentDirectory, string OutputDirectory,
    string ExpectedB1aSha256, int ExpectedRecords, Action? BeforePublish = null);
internal sealed record CombinedCatalogResult(int OutputRecords, string OutputSha256, string ContentFingerprint);

internal static class CombinedCatalogExporter
{
    private const string CatalogVersion = "B05a-combined-catalog-v1";
    private const string SemanticVersion = "B2-semantic-catalog-v1";
    private const string EnrichmentVersion = "B04-ml32m-tmdb-v2";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] FirstEight = ["movieLensId", "rawTitle", "title", "year", "imdbId", "movieLensAvgRating", "directedByRaw", "starringRaw"];
    private static readonly string[] SemanticFields = [.. FirstEight, "relevantTags", "semanticText"];
    private static readonly string[] EnrichmentFields = [.. FirstEight, "tmdbId", "genres", "averageRating", "ratingCount", "runtimeMinutes", "originalLanguage", "posterPath"];
    private static readonly string[] OutputFields = [.. SemanticFields, "tmdbId", "genres", "averageRating", "ratingCount", "runtimeMinutes", "originalLanguage", "posterPath"];

    internal static CombinedCatalogResult Export(CombinedCatalogOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        RequireAbsolute(options.SemanticDirectory, "semantic-dir"); RequireAbsolute(options.EnrichmentDirectory, "enrichment-dir"); RequireAbsolute(options.OutputDirectory, "output-dir");
        if (Directory.Exists(options.OutputDirectory) || File.Exists(options.OutputDirectory)) throw new ExportValidationException("Output directory must be new and absent.");
        var semanticJson = Path.Combine(options.SemanticDirectory, "movies-semantic.jsonl");
        var enrichmentJson = Path.Combine(options.EnrichmentDirectory, "movies-enriched.jsonl");
        var semanticManifest = Path.Combine(options.SemanticDirectory, "manifest.json");
        var enrichmentManifest = Path.Combine(options.EnrichmentDirectory, "manifest.json");
        foreach (var path in new[] { semanticJson, enrichmentJson, semanticManifest, enrichmentManifest })
            if (!File.Exists(path)) throw new ExportValidationException("A required catalog input file is missing.");
        var semanticHash = HashFile(semanticJson); var enrichmentHash = HashFile(enrichmentJson);
        var semanticManifestHash = HashFile(semanticManifest); var enrichmentManifestHash = HashFile(enrichmentManifest);
        var semanticFingerprint = ValidateManifest(semanticManifest, semanticHash, options.ExpectedB1aSha256, options.ExpectedRecords, true);
        var enrichmentFingerprint = ValidateManifest(enrichmentManifest, enrichmentHash, options.ExpectedB1aSha256, options.ExpectedRecords, false);
        var semantic = ReadRecords(semanticJson, SemanticFields, options.ExpectedRecords, cancellationToken);
        var enrichment = ReadRecords(enrichmentJson, EnrichmentFields, options.ExpectedRecords, cancellationToken);
        if (semantic.Count != enrichment.Count) throw new ExportValidationException("Catalog input record counts differ.");
        var output = options.OutputDirectory; var parent = Path.GetDirectoryName(output)!; Directory.CreateDirectory(parent);
        var staging = output.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging); var jsonPath = Path.Combine(staging, "movies-catalog.jsonl");
            WriteOutput(jsonPath, semantic, enrichment, cancellationToken);
            var outputHash = HashFile(jsonPath);
            var fingerprint = Fingerprint(options.ExpectedB1aSha256, semanticHash, enrichmentHash);
            ValidateOutput(jsonPath, options.ExpectedRecords, outputHash, cancellationToken);
            var manifestPath = Path.Combine(staging, "manifest.json");
            WriteManifest(manifestPath, options, semanticJson, semanticManifest, semanticHash, semanticManifestHash,
                enrichmentJson, enrichmentManifest, enrichmentHash, enrichmentManifestHash, semanticFingerprint, enrichmentFingerprint, outputHash, fingerprint, semantic, enrichment);
            ValidateCombinedManifest(manifestPath, outputHash, fingerprint, options.ExpectedRecords);
            options.BeforePublish?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(output) || File.Exists(output)) throw new ExportValidationException("Output directory appeared during merge.");
            Directory.Move(staging, output);
            return new CombinedCatalogResult(options.ExpectedRecords, outputHash, fingerprint);
        }
        catch { if (Directory.Exists(staging)) Directory.Delete(staging, true); throw; }
    }

    private static string ValidateManifest(string path, string jsonHash, string b1Hash, int expected, bool semantic)
    {
        try { return ValidateManifestCore(path, jsonHash, b1Hash, expected, semantic); }
        catch (ExportValidationException) { throw; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or DecoderFallbackException or FormatException or OverflowException)
        { throw new ExportValidationException("Source manifest has an invalid or malformed shape."); }
    }

    private static string ValidateManifestCore(string path, string jsonHash, string b1Hash, int expected, bool semantic)
    {
        using var doc = Parse(File.ReadAllText(path, StrictUtf8), "source manifest"); var root = doc.RootElement;
        var expectedVersion = semantic ? SemanticVersion : EnrichmentVersion;
        if (semantic)
        {
            if (String(root, "catalogVersion") != expectedVersion || String(root, "mappingVersion") != "B1a-v1" || String(root, "tagRuleVersion") != "B2-tagdl-top10-v1" || String(root, "semanticTemplateVersion") != "B2-semantic-text-v1") throw new ExportValidationException("B2 manifest version mismatch.");
            var output = root.GetProperty("output"); var counts = root.GetProperty("counts");
            if (String(output, "sha256") != jsonHash || Int(output, "recordCount") != expected || String(output, "order") != "movieLensId ascending" || Int(counts, "b1aRecords") != expected || Int(counts, "outputRecords") != expected) throw new ExportValidationException("B2 manifest output does not match its JSONL.");
            var sourceHash = String(root.GetProperty("inputs").GetProperty("b1aJsonl"), "sha256");
            var tagHash = String(root.GetProperty("inputs").GetProperty("tagdlCsv"), "sha256");
            var payload = $"{{\"catalogVersion\":\"{SemanticVersion}\",\"mappingVersion\":\"B1a-v1\",\"tagRuleVersion\":\"B2-tagdl-top10-v1\",\"semanticTemplateVersion\":\"B2-semantic-text-v1\",\"b1aSha256\":\"{sourceHash}\",\"tagdlSha256\":\"{tagHash}\"}}";
            if (sourceHash != b1Hash || String(root, "contentFingerprint") != Hash(Encoding.UTF8.GetBytes(payload))) throw new ExportValidationException("B2 manifest fingerprint or shared B1a hash mismatch.");
            return String(root, "contentFingerprint");
        }
        else
        {
            if (String(root, "mappingVersion") != expectedVersion || String(root, "cacheVersion") != "tmdb-cache-v2") throw new ExportValidationException("B04 manifest version mismatch.");
            var output = root.GetProperty("output"); var counts = root.GetProperty("counts");
            if (String(output, "sha256") != jsonHash || Int(output, "count") != expected || String(output, "order") != "movieLensId-ascending" || Int(counts, "matched") + Int(counts, "unmatched") != expected || Int(counts, "unmatchedSkipped") != Int(counts, "unmatched") || Int(counts, "noTmdbIdSkipped") > Int(counts, "matched")) throw new ExportValidationException("B04 manifest output or coverage does not match its JSONL.");
            var inputs = root.GetProperty("inputs"); var sourceHash = String(inputs.GetProperty("b1aJsonl"), "sha256");
            var cacheHash = String(root, "cacheSha256");
            var payload = $"{EnrichmentVersion}|{sourceHash}|{String(inputs.GetProperty("links"), "md5")}|{String(inputs.GetProperty("movies"), "md5")}|{String(inputs.GetProperty("ratings"), "md5")}|{cacheHash}";
            if (sourceHash != b1Hash || String(root, "contentFingerprint") != Hash(Encoding.UTF8.GetBytes(payload))) throw new ExportValidationException("B04 manifest fingerprint or shared B1a hash mismatch.");
            return String(root, "contentFingerprint");
        }
    }

    private static List<JsonDocument> ReadRecords(string path, string[] fields, int expected, CancellationToken token)
    {
        var records = new List<JsonDocument>(expected); using var reader = new StreamReader(path, StrictUtf8, false, 65536); string? line; long previous = 0;
        try
        {
            while ((line = reader.ReadLine()) is not null)
            {
                if ((records.Count & 1023) == 0) token.ThrowIfCancellationRequested();
                if (line.Length == 0) throw new ExportValidationException("Catalog JSONL contains a blank line.");
                var doc = Parse(line, "catalog record"); var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Select(x => x.Name).SequenceEqual(fields, StringComparer.Ordinal)) { doc.Dispose(); throw new ExportValidationException("Catalog record has unexpected fields or field order."); }
                var id = Int64(root, "movieLensId"); if (id <= previous || id <= 0) { doc.Dispose(); throw new ExportValidationException("Catalog IDs must be unique, positive and ascending."); }
                previous = id; ValidateRecord(root, fields); records.Add(doc);
            }
            if (records.Count != expected) throw new ExportValidationException("Catalog record count mismatch.");
            return records;
        }
        catch { foreach (var record in records) record.Dispose(); throw; }
    }

    private static void ValidateRecord(JsonElement r, string[] fields)
    {
        foreach (var name in FirstEight)
        {
            var v = r.GetProperty(name);
            var valid = name switch
            {
                "movieLensId" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
                "year" => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out _),
                "movieLensAvgRating" => v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out _),
                "directedByRaw" or "starringRaw" => v.ValueKind is JsonValueKind.String or JsonValueKind.Null,
                _ => v.ValueKind == JsonValueKind.String
            };
            if (!valid) throw new ExportValidationException($"Catalog field {name} has an invalid type or value.");
        }
        if (fields.SequenceEqual(SemanticFields))
        {
            var tags = r.GetProperty("relevantTags"); if (tags.ValueKind != JsonValueKind.Array) throw new ExportValidationException("relevantTags must be an array.");
            var tagNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tag in tags.EnumerateArray()) if (tag.ValueKind != JsonValueKind.Object || !tag.EnumerateObject().Select(p => p.Name).SequenceEqual(["name", "score"], StringComparer.Ordinal) || tag.GetProperty("name").ValueKind != JsonValueKind.String || string.IsNullOrEmpty(tag.GetProperty("name").GetString()) || !tagNames.Add(tag.GetProperty("name").GetString()!) || tag.GetProperty("score").ValueKind != JsonValueKind.Number || !tag.GetProperty("score").TryGetDouble(out var score) || !double.IsFinite(score)) throw new ExportValidationException("relevantTags contains an invalid value.");
            if (r.GetProperty("semanticText").ValueKind != JsonValueKind.String) throw new ExportValidationException("semanticText must be a string.");
        }
        else
        {
            var tmdb = r.GetProperty("tmdbId"); if (tmdb.ValueKind != JsonValueKind.Null && (tmdb.ValueKind != JsonValueKind.Number || !tmdb.TryGetInt32(out var tmdbId) || tmdbId <= 0)) throw new ExportValidationException("tmdbId is invalid.");
            var genres = r.GetProperty("genres");
            if (genres.ValueKind != JsonValueKind.Null)
            {
                if (genres.ValueKind != JsonValueKind.Array) throw new ExportValidationException("genres is invalid.");
                var values = new HashSet<string>(StringComparer.Ordinal);
                foreach (var genre in genres.EnumerateArray()) if (genre.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(genre.GetString()) || !values.Add(genre.GetString()!)) throw new ExportValidationException("genres is invalid.");
            }
            var avg = r.GetProperty("averageRating"); if (avg.ValueKind != JsonValueKind.Null && (avg.ValueKind != JsonValueKind.Number || !avg.TryGetDecimal(out var averageRating) || averageRating is < 0 or > 5)) throw new ExportValidationException("averageRating is invalid.");
            foreach (var name in new[] { "ratingCount", "runtimeMinutes" }) { var v = r.GetProperty(name); if (v.ValueKind != JsonValueKind.Null && (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || (name == "ratingCount" ? n < 0 : n <= 0))) throw new ExportValidationException($"{name} is invalid."); }
            var language = r.GetProperty("originalLanguage"); if (language.ValueKind != JsonValueKind.Null && (language.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(language.GetString()))) throw new ExportValidationException("originalLanguage is invalid.");
            var poster = r.GetProperty("posterPath"); if (poster.ValueKind != JsonValueKind.Null && (poster.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(poster.GetString()) || !poster.GetString()!.StartsWith('/'))) throw new ExportValidationException("posterPath is invalid.");
            if (tmdb.ValueKind == JsonValueKind.Null && new[] { "runtimeMinutes", "originalLanguage", "posterPath" }.Any(name => r.GetProperty(name).ValueKind != JsonValueKind.Null)) throw new ExportValidationException("TMDB detail fields require a tmdbId.");
        }
    }

    private static void WriteOutput(string path, List<JsonDocument> semantic, List<JsonDocument> enriched, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        for (var i = 0; i < semantic.Count; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested(); var s = semantic[i].RootElement; var e = enriched[i].RootElement;
            if (Int64(s, "movieLensId") != Int64(e, "movieLensId")) throw new ExportValidationException("Catalog IDs do not align.");
            foreach (var name in FirstEight) if (!SameValue(s.GetProperty(name), e.GetProperty(name))) throw new ExportValidationException($"B2/B04 value mismatch for {name} at movieLensId {Int64(s, "movieLensId")}.");
            using var writer = new Utf8JsonWriter(stream); writer.WriteStartObject();
            foreach (var name in SemanticFields) { writer.WritePropertyName(name); s.GetProperty(name).WriteTo(writer); }
            foreach (var name in EnrichmentFields.Skip(FirstEight.Length)) { writer.WritePropertyName(name); e.GetProperty(name).WriteTo(writer); }
            writer.WriteEndObject(); writer.Flush(); stream.WriteByte((byte)'\n');
        }
        stream.Flush(true);
    }

    private static void WriteManifest(string path, CombinedCatalogOptions o, string semanticPath, string semanticManifest, string semanticHash, string semanticManifestHash,
        string enrichmentPath, string enrichmentManifest, string enrichmentHash, string enrichmentManifestHash, string semanticFingerprint, string enrichmentFingerprint, string outputHash, string fingerprint,
        List<JsonDocument> semantic, List<JsonDocument> enriched)
    {
        var nullCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var field in EnrichmentFields.Skip(FirstEight.Length)) nullCounts[field] = enriched.Count(d => d.RootElement.GetProperty(field).ValueKind == JsonValueKind.Null);
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); using var w = new Utf8JsonWriter(fs);
        w.WriteStartObject(); w.WriteString("catalogVersion", CatalogVersion); w.WriteString("semanticCatalogVersion", SemanticVersion); w.WriteString("enrichmentVersion", EnrichmentVersion);
        w.WriteString("semanticContentFingerprint", semanticFingerprint); w.WriteString("enrichmentContentFingerprint", enrichmentFingerprint);
        w.WriteString("contentFingerprint", fingerprint); w.WriteString("generatedAtUtc", DateTimeOffset.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        w.WriteStartObject("inputs"); WriteInput(w, "b2Jsonl", semanticPath, semanticHash); WriteInput(w, "b2Manifest", semanticManifest, semanticManifestHash); WriteInput(w, "b04Jsonl", enrichmentPath, enrichmentHash); WriteInput(w, "b04Manifest", enrichmentManifest, enrichmentManifestHash); w.WriteEndObject();
        w.WriteString("b1aSha256", o.ExpectedB1aSha256); w.WriteStartObject("output"); w.WriteString("path", "movies-catalog.jsonl"); w.WriteString("sha256", outputHash); w.WriteNumber("recordCount", semantic.Count); w.WriteString("order", "movieLensId ascending"); w.WriteEndObject();
        w.WriteStartObject("coverage"); foreach (var field in nullCounts) { w.WriteStartObject(field.Key); w.WriteNumber("nullCount", field.Value); w.WriteNumber("presentCount", semantic.Count - field.Value); w.WriteEndObject(); } w.WriteEndObject(); w.WriteBoolean("validated", true); w.WriteEndObject(); w.Flush(); fs.Flush(true);
    }

    private static void ValidateOutput(string path, int expected, string hash, CancellationToken token)
    {
        if (HashFile(path) != hash) throw new ExportValidationException("Output hash validation failed.");
        using var reader = new StreamReader(path, StrictUtf8, false); string? line; var count = 0; long prior = 0;
        while ((line = reader.ReadLine()) is not null) { token.ThrowIfCancellationRequested(); if (line.Length == 0) throw new ExportValidationException("Output contains a blank line."); using var d = Parse(line, "combined output"); if (!d.RootElement.EnumerateObject().Select(x => x.Name).SequenceEqual(OutputFields, StringComparer.Ordinal)) throw new ExportValidationException("Combined output field order mismatch."); var id = Int64(d.RootElement, "movieLensId"); if (id <= prior) throw new ExportValidationException("Combined output ID order is invalid."); prior = id; count++; }
        if (count != expected) throw new ExportValidationException("Combined output count mismatch.");
    }

    private static void ValidateCombinedManifest(string path, string outputHash, string fingerprint, int count)
    { using var d = Parse(File.ReadAllText(path, StrictUtf8), "combined manifest"); var r = d.RootElement; if (String(r, "contentFingerprint") != fingerprint || !r.GetProperty("validated").GetBoolean() || String(r.GetProperty("output"), "sha256") != outputHash || Int(r.GetProperty("output"), "recordCount") != count) throw new ExportValidationException("Combined manifest validation failed."); }

    private static string Fingerprint(string b1, string b2, string b04) => Hash(Encoding.UTF8.GetBytes($"{{\"catalogVersion\":\"{CatalogVersion}\",\"semanticCatalogVersion\":\"{SemanticVersion}\",\"enrichmentVersion\":\"{EnrichmentVersion}\",\"b1aSha256\":\"{b1}\",\"b2JsonlSha256\":\"{b2}\",\"b04JsonlSha256\":\"{b04}\"}}"));
    private static bool SameValue(JsonElement a, JsonElement b) => a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number ? a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y) && x == y : a.ValueKind == b.ValueKind && a.ValueKind switch { JsonValueKind.Null => true, JsonValueKind.String => a.GetString() == b.GetString(), JsonValueKind.Number => a.GetRawText() == b.GetRawText(), JsonValueKind.True or JsonValueKind.False => a.GetBoolean() == b.GetBoolean(), _ => false };
    private static void WriteInput(Utf8JsonWriter w, string name, string path, string hash) { w.WriteStartObject(name); w.WriteString("path", path); w.WriteString("sha256", hash); w.WriteEndObject(); }
    private static string String(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.String ? e.GetProperty(name).GetString()! : throw new ExportValidationException($"Manifest field {name} must be a string.");
    private static int Int(JsonElement e, string name) => e.GetProperty(name).TryGetInt32(out var value) ? value : throw new ExportValidationException($"Manifest field {name} must be an integer.");
    private static long Int64(JsonElement e, string name) => e.GetProperty(name).TryGetInt64(out var value) ? value : throw new ExportValidationException($"Record field {name} must be an integer.");
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void RequireAbsolute(string path, string name) { if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException($"Option --{name} requires an absolute path."); }
    private static JsonDocument Parse(string json, string description) { try { return JsonDocument.Parse(json); } catch (JsonException ex) { throw new ExportValidationException($"Invalid {description}: {ex.Message}"); } }
}
