using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CineKros.Catalog.Importer;

/// <summary>Exports and strictly loads the complete, immutable Phase 8 bilingual catalog.</summary>
public static class FullBilingualCatalog
{
    public const string CatalogVersion = "B05a-bilingual-full-catalog-v1";
    public const string ManifestSchemaVersion = "full-bilingual-catalog-manifest-v1";
    public const string SourceCatalogSha256 = CatalogValidator.ExpectedHash;
    public const string SourceContentFingerprint = CatalogValidator.ExpectedFingerprint;
    public const string DictionarySha256 = "917ba3ea759b6d6595c78bf1ebcd3ddc547f00914156f3ab2fb5064fc549a715";
    public const string SourceTagsSha256 = "aebef2332312b06f0494bbf70535a15bad77b9f3ba4ffaf9340ad9fdab3148a4";
    public const string EnTextFormatVersion = "en-title-year-director-cast-tags-v1";
    public const string SrTextFormatVersion = "sr-title-year-director-cast-tags-latn-v1";
    public const string ProfileFingerprint = "eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe";
    public const int ExpectedMovieCount = 9730;
    public const int ExpectedSelectedTagOccurrences = 97300;
    public const int ExpectedUniqueTagCount = 993;
    private const string DictionaryReleaseId = "sr-latn-v1";
    private const string IdentityVersion = "sr-full-bilingual-catalog-identity-v1";
    private static readonly string[] OriginalFields = ["movieLensId", "rawTitle", "title", "year", "imdbId", "movieLensAvgRating", "directedByRaw", "starringRaw", "relevantTags", "semanticText", "tmdbId", "genres", "averageRating", "ratingCount", "runtimeMinutes", "originalLanguage", "posterPath"];
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions IndentedOptions = new(JsonOptions) { WriteIndented = true };

    public static async Task<FullBilingualCatalogDocument> ExportAsync(string sourceCatalogPath, string dictionaryPath, string outputRoot,
        CancellationToken cancellationToken = default)
    {
        RequireAbsolute(sourceCatalogPath); RequireAbsolute(dictionaryPath); RequireAbsolute(outputRoot);
        var fullOutput = Path.GetFullPath(outputRoot);
        if (Directory.Exists(fullOutput) || File.Exists(fullOutput)) throw new IOException("Full bilingual catalog output must be new and absent.");
        var source = await CatalogValidator.LoadAsync(sourceCatalogPath, cancellationToken);
        var rows = ReadSourceRows(sourceCatalogPath);
        var translations = LoadDictionary(dictionaryPath, rows);
        var tagNames = rows.SelectMany(x => x.Tags).Select(x => x.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var tagHash = Hash(JsonSerializer.SerializeToUtf8Bytes(tagNames));
        if (rows.Count != ExpectedMovieCount || source.Movies.Count != ExpectedMovieCount || tagNames.Length != ExpectedUniqueTagCount ||
            rows.Sum(x => x.Tags.Length) != ExpectedSelectedTagOccurrences || tagHash != SourceTagsSha256)
            throw new InvalidDataException("Canonical source movie/tag inventory differs from the approved Phase 8 baseline.");

        var parent = Path.GetDirectoryName(fullOutput) ?? throw new ArgumentException("Output root must have a parent directory.", nameof(outputRoot));
        Directory.CreateDirectory(parent);
        var stage = fullOutput + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(stage, "catalog"));
        var catalogPath = Path.Combine(stage, "catalog", "movies-catalog.jsonl");
        var enCorpus = new List<byte[]>(ExpectedMovieCount); var srCorpus = new List<byte[]>(ExpectedMovieCount);
        var selectedTags = new List<string>(ExpectedSelectedTagOccurrences); var ids = new List<long>(ExpectedMovieCount);
        using (var output = new FileStream(catalogPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var srTags = row.Tags.Select(tag => translations[tag.Name]).ToArray();
                var srText = FormatSemanticTextSr(row, srTags);
                var serialized = AppendFields(row.RawLine, srTags, srText);
                output.Write(serialized); output.WriteByte((byte)'\n');
                var en = row.Root.GetProperty("semanticText").GetString()!;
                enCorpus.Add(SerializeCorpus(row.Id, en)); srCorpus.Add(SerializeCorpus(row.Id, srText));
                ids.Add(row.Id); selectedTags.AddRange(row.Tags.Select(x => x.Name));
            }
            output.Flush(true);
        }
        var enCorpusHash = Hash(Join(enCorpus)); var srCorpusHash = Hash(Join(srCorpus));
        var idSetHash = Hash(Encoding.UTF8.GetBytes(string.Concat(ids.Select(id => id.ToString(CultureInfo.InvariantCulture) + "\n"))));
        var catalogHash = HashFile(catalogPath); var dictionaryHash = HashFile(dictionaryPath);
        var identity = MakeIdentity(catalogHash, dictionaryHash, idSetHash, enCorpusHash, srCorpusHash, tagHash);
        var manifest = MakeManifest(catalogHash, dictionaryHash, idSetHash, tagHash, enCorpusHash, srCorpusHash, identity);
        var manifestPath = Path.Combine(stage, "catalog", "manifest.json");
        WriteNew(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, IndentedOptions));
        WriteNew(Path.Combine(stage, "catalog", "content.sha256"), Encoding.ASCII.GetBytes(catalogHash + "\n"));

        // Validate the staged release before its single atomic publication move.
        _ = await LoadAsync(catalogPath, manifestPath, dictionaryPath, sourceCatalogPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(fullOutput) || File.Exists(fullOutput)) throw new IOException("Full bilingual catalog output appeared during export.");
        Directory.Move(stage, fullOutput);
        return await LoadAsync(Path.Combine(fullOutput, "catalog", "movies-catalog.jsonl"),
            Path.Combine(fullOutput, "catalog", "manifest.json"), dictionaryPath, sourceCatalogPath, cancellationToken);
    }

    public static async Task<FullBilingualCatalogDocument> LoadAsync(string catalogPath, string manifestPath, string dictionaryPath,
        string sourceCatalogPath, CancellationToken cancellationToken = default)
    {
        foreach (var path in new[] { catalogPath, manifestPath, dictionaryPath, sourceCatalogPath }) RequireAbsolute(path);
        var catalogBytes = await File.ReadAllBytesAsync(catalogPath, cancellationToken);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var catalogHash = Hash(catalogBytes); var source = await CatalogValidator.LoadAsync(sourceCatalogPath, cancellationToken);
        if (source.JsonlHash != SourceCatalogSha256 || source.Fingerprint != SourceContentFingerprint)
            throw new InvalidDataException("Full catalog source identity is not the pinned B05a release.");
        var sourceRows = ReadSourceRows(sourceCatalogPath);
        var translations = LoadDictionary(dictionaryPath, sourceRows);
        var names = sourceRows.SelectMany(x => x.Tags).Select(x => x.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var tagHash = Hash(JsonSerializer.SerializeToUtf8Bytes(names));
        var ids = sourceRows.Select(x => x.Id).ToArray();
        var idSetHash = Hash(Encoding.UTF8.GetBytes(string.Concat(ids.Select(id => id.ToString(CultureInfo.InvariantCulture) + "\n"))));
        var enCorpus = new List<byte[]>(ExpectedMovieCount); var srCorpus = new List<byte[]>(ExpectedMovieCount);
        var movies = new List<FullBilingualMovie>(ExpectedMovieCount); var tagCount = 0;
        var outputLines = SplitUtf8Lines(catalogBytes);
        if (outputLines.Count != ExpectedMovieCount) throw new InvalidDataException("Full bilingual catalog record count is not 9,730.");
        for (var i = 0; i < sourceRows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRow = sourceRows[i]; var srTags = sourceRow.Tags.Select(tag => translations[tag.Name]).ToArray();
            var srText = FormatSemanticTextSr(sourceRow, srTags);
            var expectedLine = AppendFields(sourceRow.RawLine, srTags, srText);
            var actualLine = Encoding.UTF8.GetString(outputLines[i]);
            if (!string.Equals(actualLine, Encoding.UTF8.GetString(expectedLine), StringComparison.Ordinal))
                throw new InvalidDataException($"Full catalog row {i + 1} differs from exact source fields or deterministic Serbian additions.");
            var en = sourceRow.Root.GetProperty("semanticText").GetString()!;
            enCorpus.Add(SerializeCorpus(sourceRow.Id, en)); srCorpus.Add(SerializeCorpus(sourceRow.Id, srText)); tagCount += sourceRow.Tags.Length;
            movies.Add(new FullBilingualMovie(source.Movies[i], en, srText, Array.AsReadOnly(srTags)));
        }
        var enHash = Hash(Join(enCorpus)); var srHash = Hash(Join(srCorpus)); var dictionaryHash = HashFile(dictionaryPath);
        if (tagCount != ExpectedSelectedTagOccurrences || names.Length != ExpectedUniqueTagCount || tagHash != SourceTagsSha256)
            throw new InvalidDataException("Full catalog tag inventory differs from the locked dictionary/source identity.");
        var catalogSidecar = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(catalogPath))!, "content.sha256");
        if (!File.Exists(catalogSidecar) || File.ReadAllText(catalogSidecar, Encoding.ASCII).Trim() != catalogHash)
            throw new InvalidDataException("Full catalog content sidecar does not match the actual catalog bytes.");
        var identity = MakeIdentity(catalogHash, dictionaryHash, idSetHash, enHash, srHash, tagHash);
        using var manifestDoc = JsonDocument.Parse(manifestBytes);
        var expectedManifest = MakeManifest(catalogHash, dictionaryHash, idSetHash, tagHash, enHash, srHash, identity);
        if (!JsonEquals(manifestDoc.RootElement, JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(expectedManifest, JsonOptions)).RootElement))
            throw new InvalidDataException("Full bilingual manifest is incomplete, drifted, or inconsistent with catalog contents.");
        var identityJson = JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions);
        return new FullBilingualCatalogDocument(Path.GetFullPath(catalogPath), catalogHash, Path.GetFullPath(sourceCatalogPath), source.JsonlHash,
            source.Fingerprint, Path.GetFullPath(dictionaryPath), dictionaryHash, idSetHash, enHash, srHash, identity.IdentitySha256,
            Encoding.UTF8.GetString(identityJson), movies.AsReadOnly());
    }

    public static string ComputeDocumentFingerprint(string language, string semanticText)
    {
        var format = language switch { "en" => EnTextFormatVersion, "sr" => SrTextFormatVersion, _ => throw new ArgumentException("Language must be en or sr.", nameof(language)) };
        return MultilingualPocCatalog.ComputeDocumentFingerprint(language, format, semanticText);
    }

    private static Dictionary<string, string> LoadDictionary(string dictionaryPath, IReadOnlyList<SourceRow> sourceRows)
    {
        if (HashFile(dictionaryPath) != DictionarySha256) throw new InvalidDataException("Phase 7 dictionary bytes do not match the locked full release.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(dictionaryPath))!;
        var sidecar = Path.Combine(directory, "content.sha256"); var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(sidecar) || File.ReadAllText(sidecar, Encoding.UTF8).Trim() != DictionarySha256 || !File.Exists(manifestPath))
            throw new InvalidDataException("Phase 7 dictionary sidecar or manifest is missing or inconsistent.");
        using var release = JsonDocument.Parse(File.ReadAllBytes(manifestPath)); var releaseRoot = release.RootElement;
        if (GetString(releaseRoot, "schemaVersion") != "sr-latn-full-release-v1" || GetString(releaseRoot, "releaseId") != DictionaryReleaseId ||
            GetString(releaseRoot, "sourceCatalogSha256") != SourceCatalogSha256 || GetString(releaseRoot, "sourceTagsSha256") != SourceTagsSha256 ||
            GetString(releaseRoot, "dictionarySha256") != DictionarySha256 || GetInt(releaseRoot, "movieCount") != ExpectedMovieCount ||
            GetInt(releaseRoot, "tagOccurrenceCount") != ExpectedSelectedTagOccurrences || GetInt(releaseRoot, "entryCount") != ExpectedUniqueTagCount)
            throw new InvalidDataException("Phase 7 dictionary release manifest is not the approved complete baseline.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(dictionaryPath)); var root = document.RootElement;
        if (GetString(root, "schemaVersion") != "tag-translations-sr-v1" || GetString(root, "sourceTagsSha256") != SourceTagsSha256 ||
            !root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != ExpectedUniqueTagCount)
            throw new InvalidDataException("Phase 7 dictionary structure is invalid.");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            var en = GetString(entry, "en"); var sr = GetString(entry, "sr");
            if (string.IsNullOrWhiteSpace(en) || string.IsNullOrWhiteSpace(sr) || !map.TryAdd(en, sr))
                throw new InvalidDataException("Phase 7 dictionary contains blank or duplicate mappings.");
        }
        var sourceTags = sourceRows.SelectMany(x => x.Tags).Select(x => x.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (map.Count != ExpectedUniqueTagCount || !map.Keys.Order(StringComparer.Ordinal).SequenceEqual(sourceTags, StringComparer.Ordinal) || sourceTags.Any(tag => !map.ContainsKey(tag)))
            throw new InvalidDataException("Phase 7 dictionary keys do not exactly cover all selected source tags.");
        return map;
    }

    private static List<SourceRow> ReadSourceRows(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (Hash(bytes) != SourceCatalogSha256) throw new InvalidDataException("Full catalog source file hash is not the locked B05a artifact.");
        var lines = SplitUtf8Lines(bytes); var rows = new List<SourceRow>(lines.Count); var ids = new HashSet<long>(); long previous = 0;
        foreach (var lineBytes in lines)
        {
            var line = Encoding.UTF8.GetString(lineBytes); using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
            EnsureProperties(root, OriginalFields);
            var id = root.GetProperty("movieLensId").GetInt64();
            if (id <= previous || !ids.Add(id)) throw new InvalidDataException("Full source IDs must be unique and ascending."); previous = id;
            var tagElement = root.GetProperty("relevantTags");
            if (tagElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Selected source tags must be an array.");
            var tags = tagElement.EnumerateArray().Select(tag => new Tag(GetString(tag, "name"), GetDouble(tag, "score"))).ToArray();
            if (tags.Length != 10 || tags.Any(x => string.IsNullOrWhiteSpace(x.Name) || !double.IsFinite(x.Score)))
                throw new InvalidDataException("Each B05a movie must retain exactly ten valid selected tags.");
            rows.Add(new SourceRow(id, line, root.Clone(), tags));
        }
        if (rows.Count != ExpectedMovieCount) throw new InvalidDataException("B05a source record count is not 9,730.");
        return rows;
    }

    private static string FormatSemanticTextSr(SourceRow row, IReadOnlyList<string> tags)
    {
        var director = RawName(row.Root.GetProperty("directedByRaw")); var cast = RawName(row.Root.GetProperty("starringRaw"));
        var title = row.Root.GetProperty("title").GetString() ?? throw new InvalidDataException("Source title is null.");
        var year = row.Root.GetProperty("year").GetInt32().ToString(CultureInfo.InvariantCulture);
        return $"Naslov: {title}. Godina: {year}. Režija: {director}. Glumci: {cast}. Tagovi: {(tags.Count == 0 ? "nema" : string.Join(", ", tags))}.";
    }
    private static string RawName(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
        value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(value.GetString()) ? "nepoznato" : value.GetString()!;

    private static byte[] AppendFields(string sourceLine, IReadOnlyList<string> tagsSr, string semanticTextSr)
    {
        if (sourceLine.Length == 0 || sourceLine[^1] != '}') throw new InvalidDataException("Canonical source row is not a compact JSON object.");
        var additions = JsonSerializer.SerializeToUtf8Bytes(new Additions(tagsSr.ToArray(), semanticTextSr), JsonOptions);
        var addedText = Encoding.UTF8.GetString(additions);
        if (!addedText.StartsWith('{') || !addedText.EndsWith('}')) throw new InvalidOperationException("Unable to serialize bilingual fields.");
        var combined = sourceLine[..^1] + "," + addedText[1..^1] + "}";
        return new UTF8Encoding(false, true).GetBytes(combined);
    }
    private static byte[] SerializeCorpus(long id, string text) => JsonSerializer.SerializeToUtf8Bytes(new CorpusRow(id, text), JsonOptions);
    private static byte[] Join(IEnumerable<byte[]> rows) { using var stream = new MemoryStream(); foreach (var row in rows) { stream.Write(row); stream.WriteByte((byte)'\n'); } return stream.ToArray(); }

    private static FullBilingualIdentity MakeIdentity(string catalogHash, string dictionaryHash, string idSetHash, string enHash, string srHash, string tagHash)
    {
        var payload = new IdentityPayload(IdentityVersion, CatalogVersion, catalogHash, SourceCatalogSha256, SourceContentFingerprint,
            dictionaryHash, idSetHash, tagHash, ExpectedMovieCount, ExpectedSelectedTagOccurrences, ExpectedUniqueTagCount,
            enHash, srHash, EnTextFormatVersion, SrTextFormatVersion, ProfileFingerprint);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return new FullBilingualIdentity(Hash(bytes), payload);
    }

    private static object MakeManifest(string catalogHash, string dictionaryHash, string idSetHash, string tagHash, string enHash, string srHash, FullBilingualIdentity identity) => new
    {
        schemaVersion = ManifestSchemaVersion, catalogVersion = CatalogVersion,
        sourceCatalog = new { version = CatalogValidator.CatalogVersion, sha256 = SourceCatalogSha256, contentFingerprint = SourceContentFingerprint },
        dictionary = new { releaseId = DictionaryReleaseId, sha256 = dictionaryHash, sourceTagsSha256 = tagHash, entryCount = ExpectedUniqueTagCount },
        selection = new { selectorVersion = "all-source-movies-v1", selectedIdSetSha256 = idSetHash, movieCount = ExpectedMovieCount,
            selectedTagOccurrenceCount = ExpectedSelectedTagOccurrences, uniqueSourceTagCount = ExpectedUniqueTagCount, sourceTagsSha256 = tagHash },
        output = new { path = "movies-catalog.jsonl", sha256 = catalogHash, recordCount = ExpectedMovieCount, schemaFieldCount = 19 },
        corpora = new { enSemanticTextSha256 = enHash, srSemanticTextSha256 = srHash },
        textFormats = new { en = EnTextFormatVersion, sr = SrTextFormatVersion, normalization = "unicode-nfc-case-preserving-v1" },
        embeddingProfileFingerprint = ProfileFingerprint,
        identitySha256 = identity.IdentitySha256, identityPayload = identity.Payload
    };

    private static List<byte[]> SplitUtf8Lines(byte[] bytes)
    {
        var lines = new List<byte[]>(); var start = 0;
        for (var i = 0; i < bytes.Length; i++) if (bytes[i] == (byte)'\n')
        {
            var length = i - start; if (length > 0 && bytes[i - 1] == (byte)'\r') throw new InvalidDataException("Catalog JSONL must use canonical LF line endings.");
            if (length == 0) throw new InvalidDataException("Catalog JSONL contains an empty line.");
            lines.Add(bytes.AsSpan(start, length).ToArray()); start = i + 1;
        }
        if (start != bytes.Length) throw new InvalidDataException("Catalog JSONL must end with LF.");
        return lines;
    }
    private static void EnsureProperties(JsonElement element, IReadOnlyList<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.EnumerateObject().Select(x => x.Name).SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("Catalog row fields/order differ from the exact contract.");
    }
    private static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.Object => a.EnumerateObject().Count() == b.EnumerateObject().Count() && a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var value) && JsonEquals(p.Value, value)),
            JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(p => JsonEquals(p.First, p.Second)),
            JsonValueKind.String => a.GetString() == b.GetString(), JsonValueKind.Number => a.GetRawText() == b.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => a.GetBoolean() == b.GetBoolean(), JsonValueKind.Null => true, _ => false
        };
    }
    private static string GetString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new InvalidDataException($"Required string field '{name}' is missing or malformed.");
    private static int GetInt(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
        ? number : throw new InvalidDataException($"Required integer field '{name}' is missing or malformed.");
    private static double GetDouble(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number)
        ? number : throw new InvalidDataException($"Required numeric field '{name}' is missing or malformed.");
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static void WriteNew(string path, byte[] bytes) { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(bytes); stream.Flush(true); }
    private static void RequireAbsolute(string path) { if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("All full bilingual catalog paths must be absolute."); }

    private sealed record SourceRow(long Id, string RawLine, JsonElement Root, Tag[] Tags);
    private sealed record Tag(string Name, double Score);
    private sealed record Additions(string[] TagsSr, string SemanticTextSr);
    private sealed record CorpusRow(long MovieLensId, string SemanticText);
    private sealed record IdentityPayload(string IdentityVersion, string CatalogVersion, string CatalogSha256, string SourceCatalogSha256,
        string SourceContentFingerprint, string DictionarySha256, string SelectionIdSetSha256, string SourceTagsSha256, int MovieCount,
        int SelectedTagOccurrenceCount, int UniqueSourceTagCount, string EnCorpusSha256, string SrCorpusSha256, string TextFormatVersionEn,
        string TextFormatVersionSr, string ProfileFingerprint);
    private sealed record FullBilingualIdentity(string IdentitySha256, IdentityPayload Payload);
}

public sealed record FullBilingualMovie(MovieRow Metadata, string SemanticText, string SemanticTextSr, IReadOnlyList<string> TagsSr)
{
    public long MovieLensId => Metadata.Id;
}

public sealed record FullBilingualCatalogDocument(string CatalogPath, string CatalogSha256, string SourceCatalogPath, string SourceCatalogSha256,
    string SourceContentFingerprint, string DictionaryPath, string DictionarySha256, string SelectionIdSetSha256, string EnCorpusSha256,
    string SrCorpusSha256, string IdentitySha256, string IdentityPayloadJson, IReadOnlyList<FullBilingualMovie> Movies)
{
    public string CatalogVersion => FullBilingualCatalog.CatalogVersion;
    public string EnTextFormatVersion => FullBilingualCatalog.EnTextFormatVersion;
    public string SrTextFormatVersion => FullBilingualCatalog.SrTextFormatVersion;
    public string ProfileFingerprint => FullBilingualCatalog.ProfileFingerprint;
    public int SelectedTagOccurrenceCount => FullBilingualCatalog.ExpectedSelectedTagOccurrences;
    public int UniqueSourceTagCount => FullBilingualCatalog.ExpectedUniqueTagCount;
}
