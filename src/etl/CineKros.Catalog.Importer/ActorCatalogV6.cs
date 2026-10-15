using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

[assembly: InternalsVisibleTo("CineKros.Importer.Tests")]

namespace CineKros.Catalog.Importer;

public sealed record ActorCatalogV6Release(int MovieCount, string CatalogSha256, string IdentitySha256);

/// <summary>File-only, pinned V6 publication. No database, environment, model or provider access.</summary>
public static class ActorCatalogV6
{
    public const string CatalogVersion = "CineKros-bilingual-actor-catalog-v6";
    public const string EnTextFormatVersion = "en-title-year-director-tags-v6";
    public const string SrTextFormatVersion = "sr-title-year-director-tags-latn-v6";
    public const string CurrentSha256 = "674cd1242a63c212de59ae26b8b003f1917b96dc9779f8ffcf0ba7948bb834d3";
    public const string AcceptedSha256 = "6fd22ec79d2c2b1e36b009f899b126c03a1876ce15a2871bb95033cfff410a4c";
    public const string AcceptedIdentity = "cfe69cc246e439f73c3354f77902896988df878af20bfdb8552cfd1fdf1be17e";
    public const string OrderedIdsSha256 = "af7b28f5c43cc219a9d5f62bd22716015a340f005ee9098f11f4301e13d7bac2";
    internal static readonly string[] Fields = ["movieLensId", "rawTitle", "title", "year", "imdbId", "movieLensAvgRating", "directedByRaw", "starringRaw", "relevantTags", "semanticText", "tmdbId", "genres", "averageRating", "ratingCount", "runtimeMinutes", "originalLanguage", "posterPath", "tagsSr", "semanticTextSr"];
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions ReviewOptions = new(Options) { WriteIndented = true, NewLine = "\n" };
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static Task<ActorCatalogV6Release> ExportAsync(string current, string accepted, string source, string dictionary, string outputRoot,
        CancellationToken cancellationToken = default) => ExportCoreAsync(current, accepted, source, dictionary, outputRoot, true, cancellationToken);

    // A separate internal fixture surface; the public CLI cannot disable production validation.
    internal static Task<ActorCatalogV6Release> ExportFixtureAsync(string current, string accepted, string source, string dictionary, string outputRoot,
        CancellationToken cancellationToken = default) => ExportCoreAsync(current, accepted, source, dictionary, outputRoot, false, cancellationToken);

    private static async Task<ActorCatalogV6Release> ExportCoreAsync(string current, string accepted, string source, string dictionary, string outputRoot,
        bool production, CancellationToken token)
    {
        foreach (var path in new[] { current, accepted, source, dictionary, outputRoot })
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("All V6 paths must be absolute.");
        var destination = Path.GetFullPath(outputRoot);
        if (Path.Exists(destination)) throw new IOException("V6 output must be absent.");
        token.ThrowIfCancellationRequested();
        var paths = new[] { current, accepted, source, dictionary };
        var inputBytes = new List<byte[]>();
        foreach (var path in paths) inputBytes.Add(await File.ReadAllBytesAsync(path, token));
        var hashes = inputBytes.Select(Hash).ToArray();
        var provenance = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (production)
        {
            if (!hashes.SequenceEqual(new[] { CurrentSha256, AcceptedSha256, CatalogValidator.ExpectedHash, FullBilingualCatalog.DictionarySha256 }))
                throw new InvalidDataException("V6 input bytes differ from the approved pins.");
            foreach (var (path, label) in new[] { (current, "current"), (accepted, "accepted"), (source, "source"), (dictionary, "dictionary") })
            {
                var parent = Path.GetDirectoryName(path)!;
                foreach (var filename in label == "source" ? new[] { "manifest.json" } : new[] { "manifest.json", "content.sha256" })
                {
                    var bytes = await File.ReadAllBytesAsync(Path.Combine(parent, filename), token);
                    provenance.Add(label + "/" + filename, Hash(bytes));
                    if (filename == "manifest.json")
                    {
                        _ = ReadSingle(bytes);
                        if ((label is "current" or "accepted") && Hash(bytes) != "d2ad27881798a5726a6ff3cae1ea049883cd86fcce6ff3daca173d8c879f5148" ||
                            label == "source" && Hash(bytes) != "9acfe59d43e40f562a2bab013ff5d72544c6dd93b5d2339612521d263f33463d")
                            throw new InvalidDataException("Original catalog manifest bytes differ from the audit.");
                    }
                    else if (Utf8.GetString(bytes).Trim() != (label is "current" or "accepted" ? AcceptedSha256 : label == "source" ? hashes[2] : hashes[3]))
                        throw new InvalidDataException("Original input sidecar differs from its accepted identity.");
                }
            }
            foreach (var path in new[] { current, accepted })
            {
                var manifest = ReadSingle(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(path)!, "manifest.json"), token));
                if (manifest.GetProperty("identitySha256").GetString() != AcceptedIdentity || manifest.GetProperty("output").GetProperty("sha256").GetString() != AcceptedSha256)
                    throw new InvalidDataException("Accepted bilingual manifest identity differs.");
            }
            // Existing strict loader checks the matching source manifest without changing it.
            _ = await CatalogValidator.LoadAsync(source, token);
            var release = ReadSingle(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(dictionary)!, "manifest.json"), token));
            if (release.GetProperty("schemaVersion").GetString() != "sr-latn-full-release-v1" || release.GetProperty("releaseId").GetString() != "sr-latn-v1" ||
                release.GetProperty("dictionarySha256").GetString() != hashes[3] || release.GetProperty("sourceCatalogSha256").GetString() != hashes[2] ||
                release.GetProperty("sourceTagsSha256").GetString() != FullBilingualCatalog.SourceTagsSha256 ||
                release.GetProperty("movieCount").GetInt32() != 9730 || release.GetProperty("entryCount").GetInt32() != 993 || release.GetProperty("tagOccurrenceCount").GetInt32() != 97300)
                throw new InvalidDataException("Dictionary release manifest differs.");
        }
        var rows = ReadRecords(inputBytes[0]); var acceptedRows = ReadRecords(inputBytes[1]); var sourceRows = ReadRecords(inputBytes[2]);
        var dictionaryRoot = ReadSingle(inputBytes[3]);
        if (production && (dictionaryRoot.GetProperty("schemaVersion").GetString() != "tag-translations-sr-v1" ||
            dictionaryRoot.GetProperty("sourceTagsSha256").GetString() != FullBilingualCatalog.SourceTagsSha256)) throw new InvalidDataException("Dictionary source identity differs.");
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in dictionaryRoot.GetProperty("entries").EnumerateArray())
        {
            var en = entry.GetProperty("en").GetString()!; var sr = entry.GetProperty("sr").GetString()!;
            if (string.IsNullOrWhiteSpace(en) || string.IsNullOrWhiteSpace(sr) || !translations.TryAdd(en, sr)) throw new InvalidDataException("Invalid translation dictionary.");
        }
        if (rows.Count == 0 || rows.Count != acceptedRows.Count || rows.Count != sourceRows.Count || production && rows.Count != 9730)
            throw new InvalidDataException("Input record counts differ.");
        var transformed = new List<Transformed>(); var events = new List<CastParsingEvent>(); long previous = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var row = rows[i]; RequireFields(row, Fields); RequireFields(acceptedRows[i], Fields); RequireFields(sourceRows[i], Fields[..17]);
            var id = row.GetProperty("movieLensId").GetInt64();
            if (id <= previous) throw new InvalidDataException("IDs must be positive, unique and ascending."); previous = id;
            foreach (var field in Fields) if (!Equal(row.GetProperty(field), acceptedRows[i].GetProperty(field))) throw new InvalidDataException($"Accepted field differs: {id}/{field}.");
            foreach (var field in Fields[..17]) if (!Equal(row.GetProperty(field), sourceRows[i].GetProperty(field))) throw new InvalidDataException($"Source field differs: {id}/{field}.");
            var tags = row.GetProperty("relevantTags").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToArray();
            var srTags = tags.Select(x => translations.TryGetValue(x, out var value) ? value : throw new InvalidDataException("Missing dictionary tag.")).ToArray();
            if (!row.GetProperty("tagsSr").EnumerateArray().Select(x => x.GetString()).SequenceEqual(srTags)) throw new InvalidDataException("SR tags differ from dictionary.");
            var title = row.GetProperty("title").GetString(); var year = row.GetProperty("year").GetInt32().ToString(CultureInfo.InvariantCulture);
            var director = NullableString(row.GetProperty("directedByRaw")); var raw = NullableString(row.GetProperty("starringRaw"));
            var enPrefix = $"Title: {title}. Year: {year}. Director: {Fallback(director, "unknown")}.";
            var srPrefix = $"Naslov: {title}. Godina: {year}. Režija: {Fallback(director, "nepoznato")}.";
            var enEnd = $" Tags: {(tags.Length == 0 ? "none" : string.Join(", ", tags))}.";
            var srEnd = $" Tagovi: {(srTags.Length == 0 ? "nema" : string.Join(", ", srTags))}.";
            if (row.GetProperty("semanticText").GetString() != enPrefix + $" Cast: {Fallback(raw, "unknown")}." + enEnd ||
                row.GetProperty("semanticTextSr").GetString() != srPrefix + $" Glumci: {Fallback(raw, "nepoznato")}." + srEnd)
                throw new InvalidDataException($"Old semantic template mismatch for {id}.");
            var parsed = CastNameParser.Parse(id, raw); events.AddRange(parsed.Events);
            transformed.Add(new(id, row, enPrefix + enEnd, srPrefix + srEnd, parsed.Cast));
        }
        var idsHash = Hash(Utf8.GetBytes(string.Concat(transformed.Select(x => x.Id.ToString(CultureInfo.InvariantCulture) + "\n"))));
        var vocabulary = transformed.SelectMany(x => x.Cast ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var collisions = vocabulary.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1)
            .Select(g => new { members = g.Order(StringComparer.Ordinal).Select(name => new { name, movieLensIds = transformed.Where(x => x.Cast?.Contains(name, StringComparer.Ordinal) == true).Select(x => x.Id).ToArray() }).ToArray() })
            .OrderBy(x => x.members[0].name, StringComparer.Ordinal).ToArray();
        if (production && (idsHash != OrderedIdsSha256 || transformed.Count(x => x.Cast is null) != 79 || collisions.Length != 17 ||
            translations.Count != 993 || transformed.Sum(x => x.Original.GetProperty("relevantTags").GetArrayLength()) != 97300 ||
            !translations.Keys.Order(StringComparer.Ordinal).SequenceEqual(transformed.SelectMany(x => x.Original.GetProperty("relevantTags").EnumerateArray().Select(t => t.GetProperty("name").GetString()!)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) ||
            events.Count(x => x.Kind == "reviewed-entity-override") != 1 || !events.Where(x => x.Kind == "suffix-attachment").Select(x => x.MovieLensId).SequenceEqual(new long[] { 3182, 26414 })))
            throw new InvalidDataException("V6 inventory differs from the approved source audit.");
        if (production && (events.Count(x => x.Kind == "trailing-empty-separator") != 213 ||
            !events.Where(x => x.Kind == "reviewed-empty-separator-override").Select(x => x.MovieLensId).SequenceEqual(new long[] { 3396 })))
            throw new InvalidDataException("V6 empty-separator inventory differs from the reviewed R1 audit.");
        var vocabularyBytes = Serialize(new { rulesVersion = CastNameParser.RulesVersion, names = vocabulary });
        var rules = new { version = CastNameParser.RulesVersion, separator = "literal-comma", normalization = "trim-nfc", suffix = "standalone-Jr.-attach-previous", duplicatePolicy = "ordinal-exact-first", collisionPolicy = "retained-distinct-exact-only", nullPolicy = "source-null-only", unknownEntities = "fail-review", trailingEmptySeparator = "ignore-one-final-empty-after-nonempty-cast-and-report", emptySeparatorOverride = new { movieLensId = 3396, preceding = "Caroll Spinney", following = "Bob Baker", maximumEmptyTokens = 1 }, entityOverride = new { movieLensId = 85179, raw = "Ayumu Sait&#333;", accepted = "Ayumu Saitō" } };
        var overridesBytes = Serialize(new { rulesVersion = CastNameParser.RulesVersion, rules, aliases = Array.Empty<string>(), parsingOverrides = events.Where(x => x.Kind is "reviewed-entity-override" or "reviewed-empty-separator-override").ToArray() });
        var catalogBytes = Join(transformed.Select(SerializeRow));
        var mappingHash = Hash(Join(transformed.Select(x => Serialize(new { movieLensId = x.Id, cast = x.Cast }))));
        var enHash = Hash(Join(transformed.Select(x => Serialize(new { movieLensId = x.Id, semanticText = x.En }))));
        var srHash = Hash(Join(transformed.Select(x => Serialize(new { movieLensId = x.Id, semanticText = x.Sr }))));
        var catalogHash = Hash(catalogBytes);
        var payload = new
        {
            identityVersion = "actor-catalog-identity-v1", catalogVersion = CatalogVersion, catalogSha256 = catalogHash,
            inputs = new { currentSha256 = hashes[0], acceptedSha256 = hashes[1], sourceSha256 = hashes[2], dictionarySha256 = hashes[3], sidecars = provenance },
            historicalAcceptedIdentity = production ? AcceptedIdentity : "fixture", schemaFieldCount = 20, movieCount = rows.Count,
            orderedIdSha256 = idsHash, castRulesVersion = CastNameParser.RulesVersion, castRulesSha256 = Hash(Serialize(rules)), overridesSha256 = Hash(overridesBytes),
            actorVocabularySha256 = Hash(vocabularyBytes), orderedCastMappingSha256 = mappingHash,
            enCorpusSha256 = enHash, srCorpusSha256 = srHash, textFormatEn = EnTextFormatVersion, textFormatSr = SrTextFormatVersion,
            embeddingProfileFingerprint = FullBilingualCatalog.ProfileFingerprint
        };
        var identity = Hash(Serialize(payload));
        var artifacts = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["catalog/movies-catalog.jsonl"] = catalogBytes,
            ["catalog/content.sha256"] = Utf8.GetBytes(catalogHash + "\n"),
            ["catalog/manifest.json"] = Serialize(new { schemaVersion = "actor-catalog-manifest-v1", catalogVersion = CatalogVersion, identitySha256 = identity, identityPayload = payload }),
            ["actors/actor-vocabulary.json"] = vocabularyBytes,
            ["actors/cast-parsing-overrides.json"] = overridesBytes,
            ["reports/input-reconciliation.json"] = Serialize(new { recordCount = rows.Count, comparedAcceptedFieldsPerRow = 19, comparedSourceFieldsPerRow = 17, numericLexemesEqual = true, dictionaryProjectionEqual = true, inputs = payload.inputs, currentFormat = DescribeFormat(inputBytes[0], rows.Count), acceptedFormat = DescribeFormat(inputBytes[1], rows.Count), sourceFormat = DescribeFormat(inputBytes[2], rows.Count) }),
            ["reports/transformation-report.json"] = Serialize(new { changedFields = new[] { "semanticText", "semanticTextSr" }, appendedField = "cast", schemaFieldCount = 20, movieCount = rows.Count, nullCastCount = transformed.Count(x => x.Cast is null), distinctActorCount = vocabulary.Length, orderedCastMappingSha256 = mappingHash, enCorpusSha256 = enHash, srCorpusSha256 = srHash }),
            ["reports/cast-anomalies.json"] = Serialize(new { rulesVersion = CastNameParser.RulesVersion, collisionPolicy = "retained-distinct-exact-only", ambiguousCaseInsensitiveLookup = "reject", events, collisions }),
            ["reports/reproducibility.json"] = Serialize(new { repeatProofExecuted = false, recommendation = "Independently export to a second absent destination and compare every artifact byte." }),
            ["catalog/review-sample.json"] = JsonSerializer.SerializeToUtf8Bytes(Sample(transformed), ReviewOptions)
        };
        var stage = destination + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(stage);
            foreach (var (relative, bytes) in artifacts)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(stage, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await stream.WriteAsync(bytes, token); stream.Flush(true);
            }
            // Re-read every staged artifact and independently inspect all output records before publishing.
            foreach (var (relative, bytes) in artifacts)
                if (!(await File.ReadAllBytesAsync(Path.Combine(stage, relative), token)).SequenceEqual(bytes)) throw new InvalidDataException("Staged bytes changed.");
            var verified = ReadRecords(await File.ReadAllBytesAsync(Path.Combine(stage, "catalog/movies-catalog.jsonl"), token));
            for (var i = 0; i < verified.Count; i++)
            {
                RequireFields(verified[i], [.. Fields, "cast"]);
                foreach (var field in Fields.Where(x => x is not "semanticText" and not "semanticTextSr"))
                    if (!Equal(verified[i].GetProperty(field), rows[i].GetProperty(field))) throw new InvalidDataException("Staged conservation failed.");
                if (!Equal(verified[i].GetProperty("cast"), ReadSingle(Serialize(new { cast = transformed[i].Cast })).GetProperty("cast"))) throw new InvalidDataException("Staged cast changed.");
            }
            token.ThrowIfCancellationRequested();
            Directory.Move(stage, destination);
            return new(rows.Count, catalogHash, identity);
        }
        catch
        {
            // Only this invocation's uncommitted sibling staging directory is owned here.
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            throw;
        }
    }

    internal static IReadOnlyList<JsonElement> ReadRecords(byte[] bytes)
    {
        _ = Utf8.GetString(bytes); // Reject invalid UTF-8 even in strings skipped by the tokenizer.
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowMultipleValues = true });
            var rows = new List<JsonElement>();
            if (!reader.Read()) throw new InvalidDataException("Empty JSON input.");
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                using var doc = JsonDocument.ParseValue(ref reader); RejectDuplicates(doc.RootElement);
                foreach (var item in doc.RootElement.EnumerateArray()) { if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Catalog array contains non-object."); rows.Add(item.Clone()); }
                if (reader.Read()) throw new InvalidDataException("Trailing data after catalog array.");
            }
            else
            {
                do
                {
                    if (reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException("Expected catalog object sequence.");
                    using var doc = JsonDocument.ParseValue(ref reader); RejectDuplicates(doc.RootElement); rows.Add(doc.RootElement.Clone());
                } while (reader.Read());
            }
            return rows;
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid complete JSON input.", ex); }
    }
    private static JsonElement ReadSingle(byte[] bytes)
    {
        var rows = ReadRecords(bytes);
        if (rows.Count != 1) throw new InvalidDataException("Expected one JSON object.");
        return rows[0];
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property."); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
    private static void RequireFields(JsonElement row, string[] fields)
    {
        if (!row.EnumerateObject().Select(x => x.Name).SequenceEqual(fields)) throw new InvalidDataException("Catalog field names/order differ.");
    }
    internal static bool Equal(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.Object => a.EnumerateObject().Select(x => x.Name).SequenceEqual(b.EnumerateObject().Select(x => x.Name)) && a.EnumerateObject().All(x => Equal(x.Value, b.GetProperty(x.Name))),
            JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(x => Equal(x.First, x.Second)),
            JsonValueKind.String => a.GetString() == b.GetString(),
            _ => a.GetRawText() == b.GetRawText()
        };
    }
    private static byte[] SerializeRow(Transformed row)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            foreach (var property in row.Original.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (property.Name == "semanticText") writer.WriteStringValue(row.En);
                else if (property.Name == "semanticTextSr") writer.WriteStringValue(row.Sr);
                else property.Value.WriteTo(writer);
            }
            writer.WritePropertyName("cast"); JsonSerializer.Serialize(writer, row.Cast, Options); writer.WriteEndObject();
        }
        return stream.ToArray();
    }
    private static object Sample(List<Transformed> rows)
    {
        var joint = rows.FirstOrDefault(x => x.Cast?.Contains("Brad Pitt", StringComparer.Ordinal) == true && x.Cast.Contains("George Clooney", StringComparer.Ordinal));
        var selected = rows.Where(x => x.Id is 1 or 3182 or 26414 or 85179).Concat(joint is null ? [] : new[] { joint })
            .Concat(rows.Where(x => x.Cast is null).Take(1)).Concat(rows.Take(3)).DistinctBy(x => x.Id).OrderBy(x => x.Id);
        return new { jointBradPittGeorgeClooneyMovieId = joint?.Id, limitation = "Exact recorded catalog names; not externally verified complete credits.", movies = selected.Select(x => new { movieLensId = x.Id, title = x.Original.GetProperty("title").GetString(), starringRaw = NullableString(x.Original.GetProperty("starringRaw")), cast = x.Cast, catalogRecord = ReadSingle(SerializeRow(x)), beforeEn = x.Original.GetProperty("semanticText").GetString(), afterEn = x.En, beforeSr = x.Original.GetProperty("semanticTextSr").GetString(), afterSr = x.Sr }).ToArray() };
    }
    private static string DescribeFormat(byte[] bytes, int count)
    {
        var text = Utf8.GetString(bytes);
        return text.TrimStart().StartsWith('[') ? "array" : text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == count ? "jsonl-object-sequence" : "multiline-object-sequence";
    }
    private static string? NullableString(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    private static string Fallback(string? value, string missing) => string.IsNullOrEmpty(value) ? missing : value;
    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    private static byte[] Join(IEnumerable<byte[]> rows) { using var stream = new MemoryStream(); foreach (var row in rows) { stream.Write(row); stream.WriteByte(10); } return stream.ToArray(); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string ComputeDocumentFingerprint(string language, string text) => MultilingualPocCatalog.ComputeDocumentFingerprint(language,
        language switch { "en" => EnTextFormatVersion, "sr" => SrTextFormatVersion, _ => throw new ArgumentException("Language must be en or sr.") }, text);
    private sealed record Transformed(long Id, JsonElement Original, string En, string Sr, string[]? Cast);
}
