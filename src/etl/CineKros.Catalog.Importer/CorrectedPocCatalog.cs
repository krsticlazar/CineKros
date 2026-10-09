using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CineKros.Catalog.Importer;

/// <summary>Strict validation and dictionary-delta helpers for the approved Phase 6T release.</summary>
public static class CorrectedPocCatalog
{
    public const string DatasetRelease = "poc-v2";
    public const string BaseDictionarySha256 = "a6d90f69ca39b499621487f54c4a8824c9644e4b4c55401881d19f7dcf808f85";
    public const string ApprovedMappingSha256 = "f98e2468d047feebe9546b20e1e64c6d9a4ff46c6fa454075165352c3dcc1be1";
    private const string SourceTagsSha256 = "37e53b1b7fcf06ddfa49be29017a5db85e32979aaf33e0f4a0f0f5201a1f9e80";
    private const string TextFormatVersion = "sr-title-year-director-cast-tags-latn-v1";
    private static readonly string[] OriginalFields = ["movieLensId", "rawTitle", "title", "year", "imdbId", "movieLensAvgRating", "directedByRaw", "starringRaw", "relevantTags", "semanticText", "tmdbId", "genres", "averageRating", "ratingCount", "runtimeMinutes", "originalLanguage", "posterPath"];
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions IndentedOptions = new(JsonOptions) { WriteIndented = true };

    public static string CreateCorrectedDictionary(string baseDictionaryPath, string approvedMappingPath, string outputDictionaryPath)
    {
        RequireAbsolute(baseDictionaryPath, nameof(baseDictionaryPath));
        RequireAbsolute(approvedMappingPath, nameof(approvedMappingPath));
        RequireAbsolute(outputDictionaryPath, nameof(outputDictionaryPath));
        var sidecarPath = Path.Combine(Path.GetDirectoryName(outputDictionaryPath)!, "content.sha256");
        if (File.Exists(outputDictionaryPath) || File.Exists(sidecarPath))
            throw new IOException("Corrected dictionary output already exists; select a new staging path.");
        var baseBytes = File.ReadAllBytes(baseDictionaryPath);
        if (Hash(baseBytes) != BaseDictionarySha256) throw new InvalidDataException("The released v1 dictionary hash does not match the approved base.");
        var mappingBytes = File.ReadAllBytes(approvedMappingPath);
        var mappingHash = Hash(mappingBytes);
        if (mappingHash != ApprovedMappingSha256) throw new InvalidDataException("The MAIN-approved correction mapping hash does not match.");
        using var baseDoc = JsonDocument.Parse(baseBytes);
        using var mappingDoc = JsonDocument.Parse(mappingBytes);
        ValidateMapping(mappingDoc.RootElement);
        var oldEntries = baseDoc.RootElement.GetProperty("entries").EnumerateArray().ToDictionary(x => RequiredString(x, "en"), StringComparer.Ordinal);
        var root = JsonNode.Parse(baseBytes)!.AsObject();
        var entries = root["entries"]!.AsArray();
        var replacements = mappingDoc.RootElement.GetProperty("entries").EnumerateArray().ToDictionary(x => RequiredString(x, "en"), x => x, StringComparer.Ordinal);
        foreach (var node in entries)
        {
            var entry = node!.AsObject();
            var en = entry["en"]!.GetValue<string>();
            if (!replacements.TryGetValue(en, out var approved)) continue;
            var old = oldEntries[en];
            if (RequiredString(old, "sr") != RequiredString(approved, "oldAccepted") || RequiredString(old, "machine") != RequiredString(approved, "machine"))
                throw new InvalidDataException($"Approved correction inputs do not match the immutable base entry '{en}'.");
            entry["previousAccepted"] = RequiredString(old, "sr");
            entry["sr"] = RequiredString(approved, "proposedSR");
            entry["reviewStatus"] = "reviewed";
            entry["manualOverride"] = true;
            entry["correctionProvenance"] = new JsonObject
            {
                ["proposalSha256"] = mappingHash,
                ["contextIds"] = JsonNode.Parse(approved.GetProperty("contextIds").GetRawText()),
                ["explanation"] = RequiredString(approved, "explanation"),
                ["confidence"] = approved.GetProperty("confidence").GetDouble()
            };
        }
        root["datasetRelease"] = DatasetRelease;
        root["correctionProposalSha256"] = mappingHash;
        var outputBytes = JsonSerializer.SerializeToUtf8Bytes(root, IndentedOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(outputDictionaryPath)!);
        WriteNew(outputDictionaryPath, outputBytes);
        WriteNew(sidecarPath, Encoding.UTF8.GetBytes(Hash(outputBytes) + "\n"));
        return Hash(outputBytes);
    }

    public static async Task<MultilingualPocCatalogDocument> LoadAsync(string catalogPath, string manifestPath, string dictionaryPath,
        string approvedMappingPath, string baseCatalogPath, string baseManifestPath, string baseDictionaryPath, string sourceCatalogPath,
        CancellationToken cancellationToken = default)
    {
        foreach (var path in new[] { catalogPath, manifestPath, dictionaryPath, approvedMappingPath, baseCatalogPath, baseManifestPath, baseDictionaryPath, sourceCatalogPath }) RequireAbsolute(path, nameof(catalogPath));
        var baseRelease = await MultilingualPocCatalog.LoadAsync(baseCatalogPath, baseManifestPath, baseDictionaryPath, sourceCatalogPath, cancellationToken);
        var dictBytes = await File.ReadAllBytesAsync(dictionaryPath, cancellationToken);
        var mappingBytes = await File.ReadAllBytesAsync(approvedMappingPath, cancellationToken);
        var catalogBytes = await File.ReadAllBytesAsync(catalogPath, cancellationToken);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var dictionaryHash = Hash(dictBytes); var mappingHash = Hash(mappingBytes); var catalogHash = Hash(catalogBytes);
        if (dictionaryHash != HashFile(Path.Combine(Path.GetDirectoryName(dictionaryPath)!, "content.sha256"), sidecar: true) || mappingHash != ApprovedMappingSha256)
            throw new InvalidDataException("Corrected dictionary sidecar or approved mapping hash is invalid.");
        using var baseDictDoc = JsonDocument.Parse(await File.ReadAllBytesAsync(baseDictionaryPath, cancellationToken));
        using var dictDoc = JsonDocument.Parse(dictBytes);
        using var mappingDoc = JsonDocument.Parse(mappingBytes);
        using var baseManifestDoc = JsonDocument.Parse(await File.ReadAllBytesAsync(baseManifestPath, cancellationToken));
        using var manifestDoc = JsonDocument.Parse(manifestBytes);
        ValidateMapping(mappingDoc.RootElement);
        var translations = ValidateCorrectedDictionary(baseDictDoc.RootElement, dictDoc.RootElement, mappingDoc.RootElement, mappingHash);
        var oldRows = ReadRows(baseCatalogPath).ToDictionary(x => x.GetProperty("movieLensId").GetInt64());
        var newRows = ReadRows(catalogPath).ToDictionary(x => x.GetProperty("movieLensId").GetInt64());
        if (newRows.Count != MultilingualPocCatalog.ExpectedCount || oldRows.Count != MultilingualPocCatalog.ExpectedCount || !oldRows.Keys.SequenceEqual(newRows.Keys))
            throw new InvalidDataException("Corrected catalog does not retain the exact ordered 150-film subset.");
        var sourceTags = oldRows.Values.SelectMany(x => x.GetProperty("relevantTags").EnumerateArray()).Select(x => RequiredString(x, "name")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (sourceTags.Length != 418 || Hash(JsonSerializer.SerializeToUtf8Bytes(sourceTags)) != SourceTagsSha256 || !translations.Keys.Order(StringComparer.Ordinal).SequenceEqual(sourceTags, StringComparer.Ordinal))
            throw new InvalidDataException("Corrected dictionary keys do not match the frozen 418 source tags.");

        var movies = new List<MultilingualPocMovie>(150); var enCorpus = new List<byte[]>(); var srCorpus = new List<byte[]>();
        for (var i = 0; i < oldRows.Count; i++)
        {
            var old = oldRows.ElementAt(i).Value; var row = newRows.ElementAt(i).Value;
            if (!row.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(OriginalFields.Concat(["tagsSr", "semanticTextSr"])))
                throw new InvalidDataException("Corrected catalog row contains unsupported or missing fields.");
            foreach (var field in OriginalFields)
                if (old.GetProperty(field).GetRawText() != row.GetProperty(field).GetRawText()) throw new InvalidDataException($"Corrected catalog changed original field '{field}'.");
            var en = RequiredString(row, "semanticText");
            if (string.IsNullOrWhiteSpace(en)) throw new InvalidDataException("Corrected catalog contains blank English semantic text.");
            var tagNames = row.GetProperty("relevantTags").EnumerateArray().Select(x => RequiredString(x, "name")).ToArray();
            var expectedTags = tagNames.Select(key => translations[key]).ToArray(); var storedTags = row.GetProperty("tagsSr").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (!expectedTags.SequenceEqual(storedTags, StringComparer.Ordinal)) throw new InvalidDataException("Corrected Serbian tag mapping is inconsistent with its dictionary.");
            var sr = FormatSemanticTextSr(row, storedTags);
            if (string.IsNullOrWhiteSpace(row.GetProperty("semanticTextSr").GetString()) || row.GetProperty("semanticTextSr").GetString() != sr)
                throw new InvalidDataException("Corrected Serbian semantic text is blank or not reconstructed from the approved template.");
            enCorpus.Add(SerializeCorpus(row.GetProperty("movieLensId").GetInt64(), en)); srCorpus.Add(SerializeCorpus(row.GetProperty("movieLensId").GetInt64(), sr));
            var metadata = baseRelease.Movies[i].Metadata;
            movies.Add(new MultilingualPocMovie(metadata, en, sr));
        }
        var enHash = Hash(Join(enCorpus)); var srHash = Hash(Join(srCorpus));
        var m = manifestDoc.RootElement; var oldM = baseManifestDoc.RootElement;
        if (RequiredString(m, "schemaVersion") != "bilingual-catalog-manifest-v1" || RequiredString(m, "catalogVersion") != MultilingualPocCatalog.CatalogVersion ||
            RequiredString(m, "datasetRelease") != DatasetRelease || RequiredString(m, "correctionProposalSha256") != mappingHash ||
            RequiredString(m, "sourceCatalog", "sha256") != MultilingualPocCatalog.SourceCatalogSha256 ||
            RequiredString(m, "sourceCatalog", "contentFingerprint") != MultilingualPocCatalog.SourceContentFingerprint ||
            RequiredString(m, "selection", "selectedIdSetSha256") != MultilingualPocCatalog.SelectionIdSetSha256 || RequiredInt(m, "selection", "movieCount") != 150 ||
            RequiredInt(m, "selection", "tagCount") != 418 || RequiredString(m, "selection", "sourceTagsSha256") != SourceTagsSha256 ||
            RequiredString(m, "dictionary", "sha256") != dictionaryHash || RequiredString(m, "dictionary", "sourceTagsSha256") != SourceTagsSha256 ||
            RequiredString(m, "output", "sha256") != catalogHash || RequiredInt(m, "output", "recordCount") != 150 || RequiredInt(m, "output", "schemaFieldCount") != 19 ||
            RequiredString(m, "corpora", "enSemanticTextSha256") != enHash || RequiredString(m, "corpora", "srSemanticTextSha256") != srHash)
            throw new InvalidDataException("Corrected catalog manifest does not truthfully identify the v2 data release.");
        CompareManifestRoots(oldM, m);
        var identity = new
        {
            identityVersion = MultilingualPocCatalog.IdentityVersion, catalogVersion = MultilingualPocCatalog.CatalogVersion,
            datasetRelease = DatasetRelease, catalogSha256 = catalogHash, sourceCatalogSha256 = MultilingualPocCatalog.SourceCatalogSha256,
            sourceContentFingerprint = MultilingualPocCatalog.SourceContentFingerprint, selectionIdSetSha256 = MultilingualPocCatalog.SelectionIdSetSha256,
            dictionarySha256 = dictionaryHash, enCorpusSha256 = enHash, srCorpusSha256 = srHash,
            textFormatVersionSr = MultilingualPocCatalog.SrTextFormatVersion, correctionProposalSha256 = mappingHash
        };
        var identityBytes = JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions);
        return new MultilingualPocCatalogDocument(catalogPath, catalogHash, sourceCatalogPath, MultilingualPocCatalog.SourceCatalogSha256,
            MultilingualPocCatalog.SourceContentFingerprint, MultilingualPocCatalog.SelectionIdSetSha256, dictionaryPath, dictionaryHash,
            enHash, srHash, Hash(identityBytes), Encoding.UTF8.GetString(identityBytes), movies);
    }

    public static void AddReleaseMetadata(string manifestPath, string approvedMappingPath)
    {
        var hash = HashFile(approvedMappingPath);
        if (hash != ApprovedMappingSha256) throw new InvalidDataException("The MAIN-approved mapping is not the pinned release proposal.");
        var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        manifest["datasetRelease"] = DatasetRelease;
        manifest["correctionProposalSha256"] = hash;
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, IndentedOptions));
    }

    private static Dictionary<string, string> ValidateCorrectedDictionary(JsonElement oldRoot, JsonElement root, JsonElement approvedRoot, string mappingHash)
    {
        if (RequiredString(root, "schemaVersion") != RequiredString(oldRoot, "schemaVersion") ||
            RequiredString(root, "sourceTagsSha256") != SourceTagsSha256 || root.GetProperty("entries").GetArrayLength() != 418)
            throw new InvalidDataException("Corrected dictionary root identity is invalid.");
        foreach (var property in oldRoot.EnumerateObject())
            if (property.Name != "entries" && (!root.TryGetProperty(property.Name, out var current) || !JsonEquals(property.Value, current)))
                throw new InvalidDataException("Corrected dictionary changed immutable root metadata.");
        if (!root.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal)
                .SetEquals(oldRoot.EnumerateObject().Select(x => x.Name).Append("datasetRelease").Append("correctionProposalSha256")) ||
            RequiredString(root, "datasetRelease") != DatasetRelease || RequiredString(root, "correctionProposalSha256") != mappingHash)
            throw new InvalidDataException("Corrected dictionary release/proposal metadata is invalid.");
        var approved = approvedRoot.GetProperty("entries").EnumerateArray().ToDictionary(x => RequiredString(x, "en"), StringComparer.Ordinal);
        var oldEntries = oldRoot.GetProperty("entries").EnumerateArray().ToDictionary(x => RequiredString(x, "en"), StringComparer.Ordinal);
        var corrected = root.GetProperty("entries").EnumerateArray().ToDictionary(x => RequiredString(x, "en"), StringComparer.Ordinal);
        if (oldEntries.Count != 418 || corrected.Count != 418 || !oldEntries.Keys.Order(StringComparer.Ordinal).SequenceEqual(corrected.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal) || approved.Count != 24)
            throw new InvalidDataException("Corrected dictionary does not preserve exactly the 418-key base mapping.");
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, old) in oldEntries)
        {
            var current = corrected[key]; var oldSr = RequiredString(old, "sr"); var machine = RequiredString(old, "machine");
            if (RequiredString(current, "en") != key || RequiredString(current, "machine") != machine) throw new InvalidDataException($"Machine output or key changed for '{key}'.");
            if (approved.TryGetValue(key, out var change))
            {
                if (RequiredString(change, "oldAccepted") != oldSr || RequiredString(change, "machine") != machine ||
                    RequiredString(current, "previousAccepted") != oldSr || RequiredString(current, "sr") != RequiredString(change, "proposedSR") ||
                    RequiredString(current, "reviewStatus") != "reviewed" || !current.GetProperty("manualOverride").GetBoolean() ||
                    RequiredString(current, "correctionProvenance", "proposalSha256") != mappingHash ||
                    !JsonEquals(current.GetProperty("correctionProvenance").GetProperty("contextIds"), change.GetProperty("contextIds")) ||
                    RequiredString(current, "correctionProvenance", "explanation") != RequiredString(change, "explanation") ||
                    current.GetProperty("correctionProvenance").GetProperty("confidence").GetDouble() != change.GetProperty("confidence").GetDouble() ||
                    !current.GetProperty("correctionProvenance").EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(["proposalSha256", "contextIds", "explanation", "confidence"]))
                    throw new InvalidDataException($"Corrected entry '{key}' does not match the approved provenance/mapping.");
                EnsureOnlyAllowedEntryChanges(old, current, changed: true);
            }
            else
            {
                if (RequiredString(current, "sr") != oldSr || RequiredString(current, "reviewStatus") != RequiredString(old, "reviewStatus") ||
                    current.GetProperty("manualOverride").GetBoolean() != old.GetProperty("manualOverride").GetBoolean() || current.TryGetProperty("previousAccepted", out _) || current.TryGetProperty("correctionProvenance", out _))
                    throw new InvalidDataException($"Unapproved dictionary entry '{key}' changed.");
                EnsureOnlyAllowedEntryChanges(old, current, changed: false);
            }
            if (string.IsNullOrWhiteSpace(RequiredString(current, "sr"))) throw new InvalidDataException("Corrected dictionary contains an empty translation.");
            translations.Add(key, RequiredString(current, "sr"));
        }
        return translations;
    }

    private static void EnsureOnlyAllowedEntryChanges(JsonElement old, JsonElement current, bool changed)
    {
        foreach (var property in old.EnumerateObject())
        {
            if (property.Name is "sr" or "reviewStatus" or "manualOverride") continue;
            if (!current.TryGetProperty(property.Name, out var value) || !JsonEquals(property.Value, value)) throw new InvalidDataException("Dictionary contains a change outside the approved translation fields.");
        }
        var allowed = old.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        if (changed) { allowed.Add("previousAccepted"); allowed.Add("correctionProvenance"); }
        if (current.EnumerateObject().Any(x => !allowed.Contains(x.Name))) throw new InvalidDataException("Dictionary contains an unsupported entry field.");
    }

    private static void ValidateMapping(JsonElement root)
    {
        var entries = root.GetProperty("entries").EnumerateArray().ToArray();
        if (RequiredString(root, "schemaVersion") != "sr-p6t-main-approved-corrections-v1" ||
            RequiredString(root, "baseDictionarySha256").ToLowerInvariant() != BaseDictionarySha256 || entries.Length != 24 ||
            entries.Select(x => RequiredString(x, "en")).Distinct(StringComparer.Ordinal).Count() != 24)
            throw new InvalidDataException("Approved mapping schema, base hash, or exact 24-key set is invalid.");
    }

    private static void CompareManifestRoots(JsonElement oldRoot, JsonElement root)
    {
        var allowedDifferences = new HashSet<string>(["dictionary", "output", "corpora", "datasetRelease", "correctionProposalSha256"], StringComparer.Ordinal);
        foreach (var property in oldRoot.EnumerateObject())
            if (!allowedDifferences.Contains(property.Name) && (!root.TryGetProperty(property.Name, out var current) || !JsonEquals(property.Value, current)))
                throw new InvalidDataException("Corrected manifest changed an unrelated release field.");
        if (root.EnumerateObject().Any(p => !oldRoot.TryGetProperty(p.Name, out _) && p.Name is not ("datasetRelease" or "correctionProposalSha256")))
            throw new InvalidDataException("Corrected manifest contains an unsupported root field.");
        foreach (var key in new[] { "dictionary", "output", "corpora" })
        {
            var original = oldRoot.GetProperty(key); var current = root.GetProperty(key);
            if (!original.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal)
                .SetEquals(current.EnumerateObject().Select(x => x.Name)))
                throw new InvalidDataException("Corrected manifest contains unsupported release metadata.");
            foreach (var property in original.EnumerateObject())
            {
                if (key == "dictionary" && property.Name == "sha256" || key == "output" && property.Name == "sha256" || key == "corpora" && property.Name is "enSemanticTextSha256" or "srSemanticTextSha256") continue;
                if (!current.TryGetProperty(property.Name, out var value) || !JsonEquals(property.Value, value)) throw new InvalidDataException("Corrected manifest changed immutable metadata.");
            }
        }
    }

    private static List<JsonElement> ReadRows(string path)
    {
        var list = new List<JsonElement>();
        foreach (var line in File.ReadLines(path, new UTF8Encoding(false, true)))
        {
            using var doc = JsonDocument.Parse(line);
            list.Add(doc.RootElement.Clone());
        }
        return list;
    }
    private static string FormatSemanticTextSr(JsonElement row, IReadOnlyList<string> tags)
    {
        string Raw(string name) { var x = row.GetProperty(name); return x.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || x.ValueKind == JsonValueKind.String && x.GetString()!.Length == 0 ? "nepoznato" : x.GetString()!; }
        return $"Naslov: {row.GetProperty("title").GetString()}. Godina: {row.GetProperty("year").GetInt32()}. Režija: {Raw("directedByRaw")}. Glumci: {Raw("starringRaw")}. Tagovi: {(tags.Count == 0 ? "nema" : string.Join(", ", tags))}.";
    }
    private static byte[] SerializeCorpus(long id, string text) => JsonSerializer.SerializeToUtf8Bytes(new { movieLensId = id, semanticText = text }, JsonOptions);
    private static byte[] Join(IEnumerable<byte[]> rows) { using var stream = new MemoryStream(); foreach (var row in rows) { stream.Write(row); stream.WriteByte((byte)'\n'); } return stream.ToArray(); }
    private static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.Object => a.EnumerateObject().Count() == b.EnumerateObject().Count() && a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var value) && JsonEquals(p.Value, value)),
            JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(pair => JsonEquals(pair.First, pair.Second)),
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.Number => a.GetDecimal() == b.GetDecimal(),
            JsonValueKind.True or JsonValueKind.False => a.GetBoolean() == b.GetBoolean(),
            JsonValueKind.Null => true,
            _ => false
        };
    }
    private static string RequiredString(JsonElement e, params string[] path) { foreach (var p in path) if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) throw new InvalidDataException("Corrected release metadata is incomplete."); return e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new InvalidDataException("Corrected release metadata has an invalid string."); }
    private static int RequiredInt(JsonElement e, params string[] path) { foreach (var p in path) if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) throw new InvalidDataException("Corrected release metadata is incomplete."); return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n : throw new InvalidDataException("Corrected release metadata has an invalid integer."); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static string HashFile(string path, bool sidecar) => sidecar ? File.ReadAllText(path, new UTF8Encoding(false, true)).Trim() : HashFile(path);
    private static void WriteNew(string path, byte[] bytes) { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(bytes); stream.Flush(true); }
    private static void RequireAbsolute(string path, string parameter) { if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("All corrected POC paths must be absolute.", parameter); }
}
