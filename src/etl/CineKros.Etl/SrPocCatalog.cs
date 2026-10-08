using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CineKros.TextNormalization;

namespace CineKros.Etl;

public sealed record SrPocSelectionResult(int MovieCount, int TagCount, string IdSetSha256, string SelectedSourceSha256);
public sealed record SrPocBuildResult(int MovieCount, string OutputSha256, string EnCorpusSha256, string SrCorpusSha256);

public static class SrPocCatalog
{
    public const string SourceSha256 = "8b2bad0a22fef45842176a1d9f3730be1568e367b9fc230fa0aa398bb5c26946";
    public const string SourceVersion = "B05a-combined-catalog-v1";
    public const string SourceFingerprint = "2ffad7ba703cb80543db617e742a61c88871332910185767ee96fe08a77a0be7";
    public const string SelectorVersion = "sr-poc-selection-v1";
    public const string CatalogVersion = "B05a-bilingual-catalog-v2";
    public const string FormatVersion = "sr-title-year-director-cast-tags-latn-v1";
    public const string ExpectedIdHash = "0960c2074b1848dbf9b7d374a82f345fc910f235434ef06002380949cf674529";
    private const string RuntimeLockSha256 = "2f0264292bdd1193745bded964afaa1d24a91ac4e3d7336c5673f5834ea5e90c";
    private static readonly SortedDictionary<string, string> ExpectedModelArtifacts = new(StringComparer.Ordinal)
    {
        ["config.json"] = "7036ccee5fdc00229b0c4482c1b67d017d72584c741c113021836895ae30b53a",
        ["generation_config.json"] = "69225dc7987a2833f266577d320c514e3c547eec6b5f75ca399aa8ab4d12ecd7",
        ["pytorch_model.bin"] = "1352ae4ef442420c47e9a9693a4baa2628be4579b167e04a801e57096f390196",
        ["source.spm"] = "7e262c2e51f67f8ddc3b08ca37381938ecc5cf43b52165054bb965483c3a4e68",
        ["target.spm"] = "948df13e89a108c933ffbf25f620ebf9b817cd68596179284329429469f27f8c",
        ["tokenizer_config.json"] = "03f32bb54014cfc720743d250fc5a519f2823b1d0110fa4865c76ba96bb2b188",
        ["vocab.json"] = "7824a39e5b838c2e0e1b8ef9b7e6a47821c804015dc90da0e4773712723d09a6"
    };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonWriterOptions Utf8WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] Fields = ["movieLensId", "rawTitle", "title", "year", "imdbId", "movieLensAvgRating", "directedByRaw", "starringRaw", "relevantTags", "semanticText", "tmdbId", "genres", "averageRating", "ratingCount", "runtimeMinutes", "originalLanguage", "posterPath"];

    public static SrPocSelectionResult Select(string catalogPath, string outputDirectory)
    {
        RequireAbsolute(catalogPath, "catalog"); RequireAbsolute(outputDirectory, "output-dir");
        ValidateSourceIdentity(catalogPath);
        var sourceHash = HashFile(catalogPath);
        var movies = ReadSource(catalogPath);
        if (movies.Count != 9730) throw new ExportValidationException("Immutable B05a catalog row count mismatch.");
        var selected = Choose(movies);
        var idHash = Hash(Encoding.UTF8.GetBytes(string.Join(',', selected.Select(x => x.Id.ToString(CultureInfo.InvariantCulture))) + "\n"));
        var tags = selected.SelectMany(x => x.Tags).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (selected.Count != 150 || idHash != ExpectedIdHash) throw new ExportValidationException($"The planned Serbian POC selection did not reproduce (rows={selected.Count}, strata={StrataCount(movies)}, tags={tags.Length}, idHash={idHash}, firstIds={string.Join(',', selected.Take(20).Select(x => x.Id))}).");
        if (tags.Length != 418) throw new ExportValidationException("The planned Serbian POC source-tag count did not reproduce.");
        PublishNew(outputDirectory, stage =>
        {
            WriteSourceRows(Path.Combine(stage, "selected-source.jsonl"), selected);
            WriteJson(Path.Combine(stage, "source-tags.json"), tags);
            WriteJson(Path.Combine(stage, "selection-manifest.json"), new
            {
                schemaVersion = "sr-poc-selection-manifest-v1", selectorVersion = SelectorVersion,
                sourceCatalogVersion = SourceVersion, sourceCatalogSha256 = sourceHash, sourceContentFingerprint = SourceFingerprint,
                selectedMovieCount = selected.Count, selectedTagCount = tags.Length, selectedIdSetSha256 = idHash,
                sourceTagsSha256 = HashCanonical(tags), selectedSourceJsonlSha256 = HashFile(Path.Combine(stage, "selected-source.jsonl"))
            });
        });
        return new(selected.Count, tags.Length, idHash, HashFile(Path.Combine(outputDirectory, "selected-source.jsonl")));
    }

    public static SrPocBuildResult Build(string catalogPath, string dictionaryPath, string outputDirectory) => BuildCore(catalogPath, dictionaryPath, outputDirectory, null);

    internal static SrPocBuildResult BuildForTests(string catalogPath, string dictionaryPath, string outputDirectory, Action beforePublish) => BuildCore(catalogPath, dictionaryPath, outputDirectory, beforePublish);

    private static SrPocBuildResult BuildCore(string catalogPath, string dictionaryPath, string outputDirectory, Action? beforePublish)
    {
        RequireAbsolute(catalogPath, "catalog"); RequireAbsolute(dictionaryPath, "dictionary"); RequireAbsolute(outputDirectory, "output-dir");
        ValidateSourceIdentity(catalogPath);
        var selected = Choose(ReadSource(catalogPath));
        var idHash = Hash(Encoding.UTF8.GetBytes(string.Join(',', selected.Select(x => x.Id.ToString(CultureInfo.InvariantCulture))) + "\n"));
        var tags = selected.SelectMany(x => x.Tags).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (selected.Count != 150 || idHash != ExpectedIdHash || tags.Length != 418) throw new ExportValidationException("The planned Serbian POC selection did not reproduce.");
        var dictionary = ReadDictionary(dictionaryPath, tags);
        var rows = new List<byte[]>(selected.Count);
        var enCorpus = new List<CorpusRow>(selected.Count); var srCorpus = new List<CorpusRow>(selected.Count);
        foreach (var movie in selected)
        {
            var srTags = movie.Tags.Select(tag => dictionary[tag]).ToArray();
            var director = movie.Root.GetProperty("directedByRaw"); var cast = movie.Root.GetProperty("starringRaw");
            var srText = FormatSemanticTextSr(movie.Root.GetProperty("title").GetString()!, movie.Root.GetProperty("year").GetInt32(), director, cast, srTags);
            rows.Add(SerializeRow(movie.Root, srTags, srText));
            enCorpus.Add(new(movie.Id, SerializeCorpus(movie.Id, movie.Root.GetProperty("semanticText").GetString()!)));
            srCorpus.Add(new(movie.Id, SerializeCorpus(movie.Id, srText)));
        }
        var enHash = Hash(SortJoin(enCorpus)); var srHash = Hash(SortJoin(srCorpus));
        var dictHash = HashFile(dictionaryPath); var sourceTagsHash = HashCanonical(tags);
        PublishNew(outputDirectory, stage =>
        {
            var jsonPath = Path.Combine(stage, "movies-catalog.jsonl");
            using (var stream = new FileStream(jsonPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { foreach (var row in rows) { stream.Write(row); stream.WriteByte((byte)'\n'); } stream.Flush(true); }
            var outputHash = HashFile(jsonPath);
            WriteJson(Path.Combine(stage, "manifest.json"), new
            {
                schemaVersion = "bilingual-catalog-manifest-v1", catalogVersion = CatalogVersion,
                sourceCatalog = new { version = SourceVersion, sha256 = SourceSha256, contentFingerprint = SourceFingerprint },
                selection = new { selectorVersion = SelectorVersion, selectedIdSetSha256 = idHash, movieCount = selected.Count, tagCount = tags.Length, sourceTagsSha256 = sourceTagsHash },
                dictionary = new { sha256 = dictHash, sourceTagsSha256 = sourceTagsHash, modelId = "Helsinki-NLP/opus-mt-en-sla", revision = "0bc26914f2f82c3dd5b235e420aa2c711a5ed3d8", normalizationVersion = SerbianLatinNormalizer.Version, formatVersion = FormatVersion },
                output = new { sha256 = outputHash, recordCount = selected.Count, schemaFieldCount = 19 },
                corpora = new { enSemanticTextSha256 = enHash, srSemanticTextSha256 = srHash }
            });
        }, beforePublish);
        return new(selected.Count, HashFile(Path.Combine(outputDirectory, "movies-catalog.jsonl")), enHash, srHash);
    }

    private sealed record Movie(long Id, int Year, string[] Genres, string[] Tags, string SourceLine, JsonDocument Document)
    { public JsonElement Root => Document.RootElement; }
    private sealed record CorpusRow(long Id, byte[] Json);

    private static void ValidateSourceIdentity(string catalogPath)
    {
        if (HashFile(catalogPath) != SourceSha256) throw new ExportValidationException("Immutable B05a catalog SHA-256 mismatch.");
        var manifestPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(catalogPath))!, "manifest.json");
        if (!File.Exists(manifestPath)) throw new ExportValidationException("Immutable B05a source manifest is missing.");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath)); var root = manifest.RootElement;
        if (root.GetProperty("catalogVersion").GetString() != SourceVersion || root.GetProperty("contentFingerprint").GetString() != SourceFingerprint ||
            root.GetProperty("output").GetProperty("sha256").GetString() != SourceSha256 || root.GetProperty("output").GetProperty("recordCount").GetInt32() != 9730)
            throw new ExportValidationException("Immutable B05a source manifest identity mismatch.");
    }

    private static List<Movie> ReadSource(string path)
    {
        var movies = new List<Movie>(); long previous = 0;
        foreach (var line in File.ReadLines(path, Utf8))
        {
            if (line.Length == 0) throw new ExportValidationException("Source catalog contains an empty line.");
            var document = JsonDocument.Parse(line); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Select(x => x.Name).SequenceEqual(Fields, StringComparer.Ordinal)) throw new ExportValidationException("Source catalog has unexpected schema/order.");
            var id = root.GetProperty("movieLensId").GetInt64(); var year = root.GetProperty("year").GetInt32();
            if (id <= previous) throw new ExportValidationException("Source movie IDs are not ascending and unique."); previous = id;
            var genres = root.GetProperty("genres").ValueKind == JsonValueKind.Array ? root.GetProperty("genres").EnumerateArray().Select(x => x.GetString()!).Order(StringComparer.Ordinal).ToArray() : [];
            var tags = root.GetProperty("relevantTags").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToArray();
            if (tags.Length != 10 || tags.Any(string.IsNullOrWhiteSpace)) throw new ExportValidationException("Every B05a row must contain ten valid relevant tags.");
            movies.Add(new(id, year, genres, tags, line, document));
        }
        return movies;
    }

    private sealed record BucketMovie(Movie Movie, string BucketKey, string RankHash);
    private static int StrataCount(IEnumerable<Movie> movies) => movies.Select(movie => BucketKey(movie)).Distinct(StringComparer.Ordinal).Count();
    private static string BucketKey(Movie movie)
    {
        var yearBucket = movie.Year < 1960 ? "0-pre1960" : movie.Year <= 1979 ? "1-1960-1979" : movie.Year <= 1999 ? "2-1980-1999" : movie.Year <= 2009 ? "3-2000-2009" : "4-2010plus";
        var genre = movie.Genres.FirstOrDefault() ?? string.Empty;
        return yearBucket + "|" + genre;
    }
    private static List<Movie> Choose(List<Movie> source)
    {
        var buckets = new SortedDictionary<string, Queue<Movie>>(StringComparer.Ordinal);
        foreach (var movie in source)
        {
            var key = BucketKey(movie);
            var seed = "cinekros-sr-poc-v1|" + movie.Id.ToString(CultureInfo.InvariantCulture);
            if (!buckets.TryGetValue(key, out var queue)) buckets[key] = queue = new Queue<Movie>();
            queue.Enqueue(movie);
        }
        var ordered = buckets.ToDictionary(x => x.Key, x => x.Value.Select(m =>
        {
            var seed = "cinekros-sr-poc-v1|" + m.Id.ToString(CultureInfo.InvariantCulture);
            return new BucketMovie(m, x.Key, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed))));
        }).OrderBy(x => x.RankHash, StringComparer.Ordinal).ThenBy(x => x.Movie.Id).Select(x => x.Movie).ToQueue(), StringComparer.Ordinal);
        var result = new List<Movie>(150);
        while (result.Count < 150)
        {
            var progressed = false;
            foreach (var key in ordered.Keys.Order(StringComparer.Ordinal))
                if (ordered[key].TryDequeue(out var movie)) { result.Add(movie); progressed = true; if (result.Count == 150) break; }
            if (!progressed) throw new ExportValidationException("Source cannot fill the planned POC selection.");
        }
        return result.OrderBy(x => x.Id).ToList();
    }

    private static Dictionary<string, string> ReadDictionary(string path, string[] tags)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path)); var root = doc.RootElement;
        if (root.GetProperty("schemaVersion").GetString() != "tag-translations-sr-v1" || root.GetProperty("normalizationVersion").GetString() != SerbianLatinNormalizer.Version || root.GetProperty("sourceTagsSha256").GetString() != HashCanonical(tags)) throw new ExportValidationException("Dictionary schema, normalizer, or source tag hash mismatch.");
        var translator = root.GetProperty("translator");
        if (translator.GetProperty("modelId").GetString() != "Helsinki-NLP/opus-mt-en-sla" || translator.GetProperty("revision").GetString() != "0bc26914f2f82c3dd5b235e420aa2c711a5ed3d8" || translator.GetProperty("targetToken").GetString() != ">>srp_Latn<<") throw new ExportValidationException("Dictionary translator model identity mismatch.");
        var decoding = translator.GetProperty("decoding");
        if (decoding.GetProperty("doSample").GetBoolean() || decoding.GetProperty("numBeams").GetInt32() != 4 || decoding.GetProperty("maxNewTokens").GetInt32() != 32 || decoding.GetProperty("sourceMaxTokens").GetInt32() != 512 || decoding.GetProperty("device").GetString() != "cpu") throw new ExportValidationException("Dictionary decoding identity mismatch.");
        if (translator.GetProperty("runtimeLockSha256").GetString() != RuntimeLockSha256) throw new ExportValidationException("Dictionary runtime lock identity mismatch.");
        var artifactHashes = translator.GetProperty("artifactHashes");
        if (artifactHashes.ValueKind != JsonValueKind.Object || !artifactHashes.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(ExpectedModelArtifacts.Keys, StringComparer.Ordinal) ||
            artifactHashes.EnumerateObject().Any(x => x.Value.ValueKind != JsonValueKind.String || x.Value.GetString() != ExpectedModelArtifacts[x.Name])) throw new ExportValidationException("Dictionary model artifact identity mismatch.");
        var sidecar = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "content.sha256");
        if (!File.Exists(sidecar) || File.ReadAllText(sidecar, Utf8).Trim() != HashFile(path)) throw new ExportValidationException("Dictionary must be an intact locked artifact with matching content.sha256.");
        var entries = root.GetProperty("entries").EnumerateArray().ToArray(); var keys = entries.Select(x => x.GetProperty("en").GetString()!).ToArray();
        if (!keys.Order(StringComparer.Ordinal).SequenceEqual(tags, StringComparer.Ordinal)) throw new ExportValidationException("Dictionary keys must exactly match the selected 418 tags.");
        var map = new Dictionary<string, string>(StringComparer.Ordinal); var statuses = new Dictionary<string, string>(StringComparer.Ordinal); var outputs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var en = entry.GetProperty("en").GetString()!; var sr = entry.GetProperty("sr").GetString()!; var machine = entry.GetProperty("machine").GetString(); var status = entry.GetProperty("reviewStatus").GetString()!;
            if (status is not ("auto_pass" or "reviewed") || string.IsNullOrWhiteSpace(machine) || string.IsNullOrWhiteSpace(sr) || map.ContainsKey(en)) throw new ExportValidationException("Dictionary contains unresolved, blank, or duplicate entries.");
            if (!StringComparer.Ordinal.Equals(SerbianLatinNormalizer.Normalize(sr), sr)) throw new ExportValidationException("Dictionary contains noncanonical Serbian text.");
            map.Add(en, sr); statuses.Add(en, status);
            var collisionKey = sr.ToUpperInvariant(); if (!outputs.TryGetValue(collisionKey, out var group)) outputs[collisionKey] = group = []; group.Add(en);
        }
        foreach (var group in outputs.Values.Where(group => group.Count > 1))
            if (group.Any(en => statuses[en] != "reviewed")) throw new ExportValidationException("Every translation in a Serbian collision group must be reviewed.");
        return map;
    }

    internal static Dictionary<string, string> ReadDictionaryForTests(string path, string[] tags) => ReadDictionary(path, tags);

    private static byte[] SerializeRow(JsonElement source, string[] srTags, string srText)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); foreach (var field in Fields) { writer.WritePropertyName(field); source.GetProperty(field).WriteTo(writer); }
            writer.WriteStartArray("tagsSr"); foreach (var tag in srTags) writer.WriteStringValue(tag); writer.WriteEndArray(); writer.WriteString("semanticTextSr", srText); writer.WriteEndObject();
        }
        return stream.ToArray();
    }
    private static byte[] SerializeCorpus(long id, string text) { using var s = new MemoryStream(); using (var w = new Utf8JsonWriter(s, Utf8WriterOptions)) { w.WriteStartObject(); w.WriteNumber("movieLensId", id); w.WriteString("semanticText", text); w.WriteEndObject(); } return s.ToArray(); }
    private static byte[] SortJoin(IEnumerable<CorpusRow> rows) { using var s = new MemoryStream(); foreach (var row in rows.OrderBy(x => x.Id)) { s.Write(row.Json); s.WriteByte((byte)'\n'); } return s.ToArray(); }
    internal static string FormatSemanticTextSr(string title, int year, JsonElement director, JsonElement cast, IReadOnlyList<string> tags)
    {
        var tagText = tags.Count == 0 ? "nema" : string.Join(", ", tags);
        return $"Naslov: {title}. Godina: {year.ToString(CultureInfo.InvariantCulture)}. Režija: {RawName(director)}. Glumci: {RawName(cast)}. Tagovi: {tagText}.";
    }
    private static string RawName(JsonElement element) => element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || (element.ValueKind == JsonValueKind.String && element.GetString()!.Length == 0) ? "nepoznato" : element.GetString()!;
    private static void WriteSourceRows(string path, IReadOnlyList<Movie> movies) { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); foreach (var movie in movies) { var bytes = Utf8.GetBytes(movie.SourceLine); stream.Write(bytes); stream.WriteByte((byte)'\n'); } stream.Flush(true); }
    private static string HashCanonical(string[] tags) => Hash(JsonSerializer.SerializeToUtf8Bytes(tags.Order(StringComparer.Ordinal)));
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void WriteJson<T>(string path, T value) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true }));
    private static void RequireAbsolute(string path, string option) { if (!Path.IsPathFullyQualified(path)) throw new ArgumentException($"Option --{option} requires an absolute path."); }
    private static void PublishNew(string target, Action<string> create, Action? beforePublish = null)
    {
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Output path must be new and absent.");
        var parent = Path.GetDirectoryName(Path.GetFullPath(target))!; Directory.CreateDirectory(parent); var stage = target + ".staging-" + Guid.NewGuid().ToString("N");
        try { Directory.CreateDirectory(stage); create(stage); beforePublish?.Invoke(); if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Output path appeared during publication."); Directory.Move(stage, target); }
        catch { if (Directory.Exists(stage)) Directory.Delete(stage, true); throw; }
    }
}

internal static class QueueExtensions { public static Queue<T> ToQueue<T>(this IEnumerable<T> values) => new(values); }
