using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CineKros.Catalog.Importer;

public sealed record ActorMovieV6(long MovieLensId, string SemanticText, string SemanticTextSr)
{
    // Additive V6 projection. Ratings retain their original JSON numeric lexeme.
    public MovieRow? Metadata { get; init; }
    public IReadOnlyList<string>? Cast { get; init; }
}
public sealed record ActorCatalogV6Document(string CatalogPath, string CatalogSha256, string SourceCatalogSha256,
    string SourceContentFingerprint, string DictionarySha256, string SelectionIdSetSha256, string EnCorpusSha256,
    string SrCorpusSha256, string IdentitySha256, string ActorVocabularySha256, string OrderedCastMappingSha256,
    string OverridesSha256, string CastRulesSha256, IReadOnlyList<ActorMovieV6> Movies)
{
    public string CatalogVersion => ActorCatalogV6.CatalogVersion;
    public string EnTextFormatVersion => ActorCatalogV6.EnTextFormatVersion;
    public string SrTextFormatVersion => ActorCatalogV6.SrTextFormatVersion;
    public string ProfileFingerprint => FullBilingualCatalog.ProfileFingerprint;
}

/// <summary>Read-only verification of the immutable, manually approved V6 release.</summary>
public static class ActorCatalogV6Reader
{
    public const string CatalogSha256 = "30b9b7ee9ef0f94cb1427e4b1921a296ba3692c621ac869c17e8c45dff3a7925";
    public const string ManifestSha256 = "905da8392dadb3ad3c2f6ab75ef595d950ec54a5fb87af5ee76dd894252b14c7";
    public const string IdentitySha256 = "03e452c345576b5f369834e89016d43902259aa1903697b8b92123e4ea66f5d3";
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static Task<ActorCatalogV6Document> LoadAsync(string catalog, string manifest, string dictionary, string source,
        CancellationToken token = default) => LoadCoreAsync(catalog, manifest, dictionary, source, true, token);
    internal static Task<ActorCatalogV6Document> LoadFixtureAsync(string catalog, string manifest, string dictionary, string source,
        CancellationToken token = default) => LoadCoreAsync(catalog, manifest, dictionary, source, false, token);

    private static async Task<ActorCatalogV6Document> LoadCoreAsync(string catalog, string manifest, string dictionary, string source, bool production, CancellationToken token)
    {
        foreach (var path in new[] { catalog, manifest, dictionary, source })
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("All V6 reader paths must be absolute.");
        var bytes = await File.ReadAllBytesAsync(catalog, token);
        var manifestBytes = await File.ReadAllBytesAsync(manifest, token);
        var dictionaryBytes = await File.ReadAllBytesAsync(dictionary, token);
        var sourceBytes = await File.ReadAllBytesAsync(source, token);
        var hash = Hash(bytes); var dictionaryHash = Hash(dictionaryBytes); var sourceHash = Hash(sourceBytes);
        if (production && (hash != CatalogSha256 || Hash(manifestBytes) != ManifestSha256 ||
            dictionaryHash != FullBilingualCatalog.DictionarySha256 || sourceHash != CatalogValidator.ExpectedHash))
            throw new InvalidDataException("V6 release/input bytes differ from approved immutable pins.");
        if (!File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(catalog)!, "content.sha256")).SequenceEqual(Encoding.ASCII.GetBytes(hash + "\n")))
            throw new InvalidDataException("V6 content sidecar mismatch.");
        var root = Single(manifestBytes); var payload = root.GetProperty("identityPayload");
        var identity = Hash(JsonSerializer.SerializeToUtf8Bytes(payload, Options));
        if (root.GetProperty("schemaVersion").GetString() != "actor-catalog-manifest-v1" ||
            root.GetProperty("catalogVersion").GetString() != ActorCatalogV6.CatalogVersion ||
            root.GetProperty("identitySha256").GetString() != identity || production && identity != IdentitySha256)
            throw new InvalidDataException("V6 manifest/identity mismatch.");
        Check(payload, "identityVersion", "actor-catalog-identity-v1"); Check(payload, "catalogVersion", ActorCatalogV6.CatalogVersion);
        Check(payload, "catalogSha256", hash); Check(payload, "textFormatEn", ActorCatalogV6.EnTextFormatVersion);
        Check(payload, "textFormatSr", ActorCatalogV6.SrTextFormatVersion); Check(payload, "embeddingProfileFingerprint", FullBilingualCatalog.ProfileFingerprint);
        Check(payload, "castRulesVersion", CastNameParser.RulesVersion);
        Check(payload.GetProperty("inputs"), "sourceSha256", sourceHash); Check(payload.GetProperty("inputs"), "dictionarySha256", dictionaryHash);
        if (payload.GetProperty("schemaFieldCount").GetInt32() != 20) throw new InvalidDataException("V6 schema is not twenty fields.");
        var actors = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(catalog))!, "actors");
        var vocabularyBytes = await File.ReadAllBytesAsync(Path.Combine(actors, "actor-vocabulary.json"), token);
        var overridesBytes = await File.ReadAllBytesAsync(Path.Combine(actors, "cast-parsing-overrides.json"), token);
        Check(payload, "actorVocabularySha256", Hash(vocabularyBytes)); Check(payload, "overridesSha256", Hash(overridesBytes));
        var vocabulary = Single(vocabularyBytes); var overrides = Single(overridesBytes);
        Check(vocabulary, "rulesVersion", CastNameParser.RulesVersion); Check(overrides, "rulesVersion", CastNameParser.RulesVersion);
        Check(payload, "castRulesSha256", Hash(JsonSerializer.SerializeToUtf8Bytes(overrides.GetProperty("rules"), Options)));
        if (overrides.GetProperty("aliases").GetArrayLength() != 0) throw new InvalidDataException("V6 aliases are frozen empty.");
        var rows = ActorCatalogV6.ReadRecords(bytes); var sources = ActorCatalogV6.ReadRecords(sourceBytes);
        var map = Single(dictionaryBytes).GetProperty("entries").EnumerateArray().ToDictionary(x => x.GetProperty("en").GetString()!, x => x.GetProperty("sr").GetString()!, StringComparer.Ordinal);
        if (rows.Count != sources.Count || rows.Count != payload.GetProperty("movieCount").GetInt32() || production && rows.Count != 9730)
            throw new InvalidDataException("V6 count mismatch.");
        var movies = new List<ActorMovieV6>(); var names = new HashSet<string>(StringComparer.Ordinal);
        using var en = new MemoryStream(); using var sr = new MemoryStream(); using var mapping = new MemoryStream();
        var ids = new StringBuilder(); long previous = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var row = rows[i]; var original = sources[i];
            if (!row.EnumerateObject().Select(x => x.Name).SequenceEqual(ActorCatalogV6.Fields.Append("cast"), StringComparer.Ordinal))
                throw new InvalidDataException("V6 exact ordered fields differ.");
            var id = row.GetProperty("movieLensId").GetInt64();
            if (id <= previous) throw new InvalidDataException("V6 IDs must be positive, unique and ascending."); previous = id;
            foreach (var field in ActorCatalogV6.Fields[..17].Where(x => x != "semanticText"))
                if (!ActorCatalogV6.Equal(row.GetProperty(field), original.GetProperty(field))) throw new InvalidDataException($"V6 metadata conservation failed: {id}/{field}.");
            var tags = row.GetProperty("relevantTags").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToArray();
            var tagsSr = tags.Select(x => map[x]).ToArray();
            if (!row.GetProperty("tagsSr").EnumerateArray().Select(x => x.GetString()).SequenceEqual(tagsSr)) throw new InvalidDataException("V6 dictionary projection mismatch.");
            var title = row.GetProperty("title").GetString(); var year = row.GetProperty("year").GetInt32().ToString(CultureInfo.InvariantCulture);
            var director = row.GetProperty("directedByRaw").GetString();
            var enText = $"Title: {title}. Year: {year}. Director: {(string.IsNullOrEmpty(director) ? "unknown" : director)}. Tags: {(tags.Length == 0 ? "none" : string.Join(", ", tags))}.";
            var srText = $"Naslov: {title}. Godina: {year}. Režija: {(string.IsNullOrEmpty(director) ? "nepoznato" : director)}. Tagovi: {(tagsSr.Length == 0 ? "nema" : string.Join(", ", tagsSr))}.";
            if (row.GetProperty("semanticText").GetString() != enText || row.GetProperty("semanticTextSr").GetString() != srText)
                throw new InvalidDataException("V6 no-cast text template mismatch.");
            var cast = CastNameParser.Parse(id, row.GetProperty("starringRaw").GetString()).Cast;
            if (!ActorCatalogV6.Equal(row.GetProperty("cast"), JsonSerializer.SerializeToElement(cast, Options))) throw new InvalidDataException("V6 independent cast mapping mismatch.");
            foreach (var name in cast ?? []) names.Add(name);
            Append(mapping, new { movieLensId = id, cast }); Append(en, new { movieLensId = id, semanticText = enText }); Append(sr, new { movieLensId = id, semanticText = srText });
            var metadata = new MovieRow(id, row.GetProperty("imdbId").GetString()!, NullableLong(row, "tmdbId"),
                title!, row.GetProperty("year").GetInt32(), NullableInt(row, "runtimeMinutes"), row.GetProperty("originalLanguage").GetString(),
                row.GetProperty("genres").ValueKind == JsonValueKind.Null ? null : Array.AsReadOnly(row.GetProperty("genres").EnumerateArray().Select(x => x.GetString()!).ToArray()),
                row.GetProperty("averageRating").ValueKind == JsonValueKind.Null ? null : row.GetProperty("averageRating").GetRawText(),
                NullableLong(row, "ratingCount"), row.GetProperty("posterPath").GetString());
            ids.Append(id.ToString(CultureInfo.InvariantCulture)).Append('\n'); movies.Add(new(id, enText, srText)
            { Metadata = metadata, Cast = cast is null ? null : Array.AsReadOnly(cast.ToArray()) });
        }
        var idsHash = Hash(Encoding.UTF8.GetBytes(ids.ToString())); var enHash = Hash(en.ToArray()); var srHash = Hash(sr.ToArray()); var mappingHash = Hash(mapping.ToArray());
        Check(payload, "orderedIdSha256", idsHash); Check(payload, "enCorpusSha256", enHash); Check(payload, "srCorpusSha256", srHash); Check(payload, "orderedCastMappingSha256", mappingHash);
        if (production && idsHash != ActorCatalogV6.OrderedIdsSha256 || !vocabulary.GetProperty("names").EnumerateArray().Select(x => x.GetString()).SequenceEqual(names.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("V6 ID/vocabulary inventory mismatch.");
        return new(Path.GetFullPath(catalog), hash, sourceHash, FullBilingualCatalog.SourceContentFingerprint, dictionaryHash, idsHash, enHash, srHash, identity,
            Hash(vocabularyBytes), mappingHash, Hash(overridesBytes), payload.GetProperty("castRulesSha256").GetString()!, movies.AsReadOnly());
    }
    public static JsonElement ReadStrictObject(byte[] bytes) => Single(bytes);
    private static JsonElement Single(byte[] bytes)
    {
        var rows = ActorCatalogV6.ReadRecords(bytes);
        if (rows.Count != 1 || bytes.Length == 0 || bytes[0] != (byte)'{') throw new InvalidDataException("Expected one strict JSON object.");
        return rows[0];
    }
    private static void Check(JsonElement node, string key, string expected)
    {
        if (node.GetProperty(key).GetString() != expected) throw new InvalidDataException($"V6 identity mismatch: {key}.");
    }
    private static void Append<T>(Stream stream, T value) { stream.Write(JsonSerializer.SerializeToUtf8Bytes(value, Options)); stream.WriteByte(10); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static long? NullableLong(JsonElement row, string name) => row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetInt64();
    private static int? NullableInt(JsonElement row, string name) => row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetInt32();
}
